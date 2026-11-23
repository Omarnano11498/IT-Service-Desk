/* =============================================================================
   IT Service Desk - SQL Server Setup & Seed Script
   Target : Microsoft SQL Server 2016+ / Azure SQL / LocalDB / SQL Express
   Model  : 3NF relational schema, UTC timestamps (DATETIME2), immutable audit log
   Usage  : Run the whole script in SSMS / Azure Data Studio / sqlcmd:
            sqlcmd -S localhost -E -i ServiceDesk_Setup.sql
            sqlcmd -S 192.168.1.50,1433 -U sa -P <pwd> -C -i ServiceDesk_Setup.sql
   WARNING: Drops and recreates the [ServiceDesk] database.
   Default admin: username = admin   password = Admin@12345   (CHANGE IT after first login)
   ============================================================================= */

SET NOCOUNT ON;
USE master;
GO

IF DB_ID(N'ServiceDesk') IS NOT NULL
BEGIN
    ALTER DATABASE ServiceDesk SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE ServiceDesk;
END
GO

CREATE DATABASE ServiceDesk;
GO
ALTER DATABASE ServiceDesk SET READ_COMMITTED_SNAPSHOT ON; -- readers do not block writers (queue refresh vs. triage)
GO

USE ServiceDesk;
GO

/* ------------------------------------------------------------------------------
   1. LOOKUP TABLES
   ------------------------------------------------------------------------------ */
CREATE TABLE dbo.Roles
(
    RoleId      TINYINT       NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    RoleName    NVARCHAR(50)  NOT NULL CONSTRAINT UQ_Roles_RoleName UNIQUE
);

CREATE TABLE dbo.TicketStatuses
(
    StatusId    TINYINT       NOT NULL CONSTRAINT PK_TicketStatuses PRIMARY KEY,
    StatusName  NVARCHAR(30)  NOT NULL CONSTRAINT UQ_TicketStatuses_Name UNIQUE,
    IsOpen      BIT           NOT NULL
);

/* Priority + its SLA targets live together: PriorityId is the key, targets depend only on it (3NF). */
CREATE TABLE dbo.SlaPolicies
(
    PriorityId              TINYINT       NOT NULL CONSTRAINT PK_SlaPolicies PRIMARY KEY,
    PriorityName            NVARCHAR(20)  NOT NULL CONSTRAINT UQ_SlaPolicies_Name UNIQUE,
    ResponseTargetMinutes   INT           NOT NULL,
    ResolutionTargetMinutes INT           NOT NULL,
    IsActive                BIT           NOT NULL CONSTRAINT DF_SlaPolicies_IsActive DEFAULT (1),
    UpdatedAt               DATETIME2(0)  NOT NULL CONSTRAINT DF_SlaPolicies_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SlaPolicies_Targets CHECK (ResponseTargetMinutes > 0
                                         AND ResolutionTargetMinutes >= ResponseTargetMinutes)
);

CREATE TABLE dbo.TicketCategories
(
    CategoryId   SMALLINT      NOT NULL IDENTITY(1,1) CONSTRAINT PK_TicketCategories PRIMARY KEY,
    CategoryName NVARCHAR(80)  NOT NULL CONSTRAINT UQ_TicketCategories_Name UNIQUE,
    CategoryType TINYINT       NOT NULL,          -- 1 Hardware, 2 Software, 3 Network, 4 General (drives the C# factory)
    Description  NVARCHAR(400) NULL,
    IsActive     BIT           NOT NULL CONSTRAINT DF_TicketCategories_IsActive DEFAULT (1),
    CONSTRAINT CK_TicketCategories_Type CHECK (CategoryType BETWEEN 1 AND 4)
);

/* ------------------------------------------------------------------------------
   2. USERS (End users + IT administrators, role-discriminated)
   ------------------------------------------------------------------------------ */
CREATE TABLE dbo.Users
(
    UserId          INT            NOT NULL IDENTITY(1,1) CONSTRAINT PK_Users PRIMARY KEY,
    RoleId          TINYINT        NOT NULL CONSTRAINT FK_Users_Roles REFERENCES dbo.Roles(RoleId),
    UserName        NVARCHAR(50)   NULL,      -- admins only
    FullName        NVARCHAR(120)  NOT NULL,
    Email           NVARCHAR(254)  NOT NULL,
    Phone           NVARCHAR(30)   NULL,
    Department      NVARCHAR(80)   NULL,
    PasswordHash    VARBINARY(64)  NULL,      -- PBKDF2-HMAC-SHA256 derived key (admins only)
    PasswordSalt    VARBINARY(32)  NULL,
    HashIterations  INT            NULL,
    IsActive        BIT            NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    CreatedAt       DATETIME2(0)   NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSUTCDATETIME()),
    LastLoginAt     DATETIME2(0)   NULL,
    /* End users never carry credentials; admins must carry a complete credential triple. */
    CONSTRAINT CK_Users_Credentials CHECK (
        (RoleId = 1 AND UserName IS NULL AND PasswordHash IS NULL AND PasswordSalt IS NULL AND HashIterations IS NULL)
     OR (RoleId = 2 AND UserName IS NOT NULL AND PasswordHash IS NOT NULL AND PasswordSalt IS NOT NULL AND HashIterations >= 100000)
    ),
    CONSTRAINT CK_Users_Email CHECK (Email LIKE N'%_@_%._%')
);
CREATE UNIQUE INDEX UX_Users_UserName      ON dbo.Users(UserName) WHERE UserName IS NOT NULL;
CREATE UNIQUE INDEX UX_Users_EndUserEmail  ON dbo.Users(Email)    WHERE RoleId = 1;   -- one requester row per email

/* ------------------------------------------------------------------------------
   3. TICKETS  (Table-per-hierarchy: subtype columns are nullable and type-checked)
   ------------------------------------------------------------------------------ */
CREATE TABLE dbo.Tickets
(
    TicketId           INT            NOT NULL IDENTITY(1,1) CONSTRAINT PK_Tickets PRIMARY KEY,
    TicketNumber       AS (CONVERT(VARCHAR(12), 'TKT-' + RIGHT('0000000' + CONVERT(VARCHAR(10), TicketId), 7))) PERSISTED,
    RequesterId        INT            NOT NULL CONSTRAINT FK_Tickets_Requester REFERENCES dbo.Users(UserId),
    CategoryId         SMALLINT       NOT NULL CONSTRAINT FK_Tickets_Category  REFERENCES dbo.TicketCategories(CategoryId),
    PriorityId         TINYINT        NOT NULL CONSTRAINT FK_Tickets_Priority  REFERENCES dbo.SlaPolicies(PriorityId),
    StatusId           TINYINT        NOT NULL CONSTRAINT FK_Tickets_Status    REFERENCES dbo.TicketStatuses(StatusId),
    Urgency            TINYINT        NOT NULL,   -- 1 High, 2 Medium, 3 Low
    Impact             TINYINT        NOT NULL,   -- 1 High, 2 Medium, 3 Low
    Subject            NVARCHAR(200)  NOT NULL,
    Description        NVARCHAR(MAX)  NOT NULL,
    AssignedAdminId    INT            NULL     CONSTRAINT FK_Tickets_Admin     REFERENCES dbo.Users(UserId),
    CreatedAt          DATETIME2(0)   NOT NULL CONSTRAINT DF_Tickets_CreatedAt DEFAULT (SYSUTCDATETIME()),
    FirstResponseAt    DATETIME2(0)   NULL,
    ResponseDueAt      DATETIME2(0)   NOT NULL,
    ResolutionDueAt    DATETIME2(0)   NOT NULL,
    ResolvedAt         DATETIME2(0)   NULL,
    ClosedAt           DATETIME2(0)   NULL,
    ResolutionNotes    NVARCHAR(MAX)  NULL,
    RootCause          NVARCHAR(1000) NULL,
    -- Hardware subtype
    AssetTag           NVARCHAR(50)   NULL,
    DeviceType         NVARCHAR(60)   NULL,
    -- Software subtype
    ApplicationName    NVARCHAR(100)  NULL,
    ApplicationVersion NVARCHAR(40)   NULL,
    -- Network subtype
    NetworkLocation    NVARCHAR(100)  NULL,
    AffectedIpAddress  VARCHAR(45)    NULL,
    RowVersion         ROWVERSION     NOT NULL,   -- optimistic concurrency between admins
    CONSTRAINT CK_Tickets_Urgency    CHECK (Urgency BETWEEN 1 AND 3),
    CONSTRAINT CK_Tickets_Impact     CHECK (Impact  BETWEEN 1 AND 3),
    CONSTRAINT CK_Tickets_Dates      CHECK (ResolutionDueAt >= ResponseDueAt AND ResponseDueAt >= CreatedAt
                                        AND (FirstResponseAt IS NULL OR FirstResponseAt >= CreatedAt)
                                        AND (ResolvedAt      IS NULL OR ResolvedAt      >= CreatedAt)
                                        AND (ClosedAt        IS NULL OR (ResolvedAt IS NOT NULL AND ClosedAt >= ResolvedAt))),
    CONSTRAINT CK_Tickets_Resolution CHECK (ResolvedAt IS NULL OR LEN(ResolutionNotes) > 0)
);
CREATE UNIQUE INDEX UX_Tickets_TicketNumber ON dbo.Tickets(TicketNumber);
CREATE INDEX IX_Tickets_Queue     ON dbo.Tickets(StatusId, PriorityId, ResolutionDueAt) INCLUDE (CreatedAt, AssignedAdminId, CategoryId);
CREATE INDEX IX_Tickets_Requester ON dbo.Tickets(RequesterId);
CREATE INDEX IX_Tickets_Admin     ON dbo.Tickets(AssignedAdminId) WHERE AssignedAdminId IS NOT NULL;
CREATE INDEX IX_Tickets_Category  ON dbo.Tickets(CategoryId);
CREATE INDEX IX_Tickets_CreatedAt ON dbo.Tickets(CreatedAt) INCLUDE (StatusId, PriorityId, ResolvedAt, FirstResponseAt);

/* ------------------------------------------------------------------------------
   4. TICKET HISTORY (immutable audit trail)
   ------------------------------------------------------------------------------ */
CREATE TABLE dbo.TicketHistory
(
    HistoryId         BIGINT         NOT NULL IDENTITY(1,1) CONSTRAINT PK_TicketHistory PRIMARY KEY,
    TicketId          INT            NOT NULL CONSTRAINT FK_TicketHistory_Ticket REFERENCES dbo.Tickets(TicketId),
    ActionType        TINYINT        NOT NULL,  -- 1 Created,2 Assigned,3 StatusChanged,4 PriorityChanged,5 NoteAdded,6 Resolved,7 Closed,8 Reopened,9 Escalated,10 Rated
    FromStatusId      TINYINT        NULL CONSTRAINT FK_TicketHistory_FromStatus REFERENCES dbo.TicketStatuses(StatusId),
    ToStatusId        TINYINT        NULL CONSTRAINT FK_TicketHistory_ToStatus   REFERENCES dbo.TicketStatuses(StatusId),
    FromPriorityId    TINYINT        NULL CONSTRAINT FK_TicketHistory_FromPrio   REFERENCES dbo.SlaPolicies(PriorityId),
    ToPriorityId      TINYINT        NULL CONSTRAINT FK_TicketHistory_ToPrio     REFERENCES dbo.SlaPolicies(PriorityId),
    PerformedByUserId INT            NULL CONSTRAINT FK_TicketHistory_User       REFERENCES dbo.Users(UserId),
    Notes             NVARCHAR(2000) NULL,
    IsInternal        BIT            NOT NULL CONSTRAINT DF_TicketHistory_IsInternal DEFAULT (0), -- hidden from requester
    OccurredAt        DATETIME2(0)   NOT NULL CONSTRAINT DF_TicketHistory_OccurredAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_TicketHistory_Action CHECK (ActionType BETWEEN 1 AND 10)
);
CREATE INDEX IX_TicketHistory_Ticket ON dbo.TicketHistory(TicketId, OccurredAt) INCLUDE (ActionType, ToStatusId);
GO

CREATE TRIGGER dbo.TR_TicketHistory_Immutable
ON dbo.TicketHistory
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, N'TicketHistory is an immutable audit log: UPDATE and DELETE are not permitted.', 1;
END
GO

/* ------------------------------------------------------------------------------
   5. SERVICE EVALUATIONS (one snapshot per ticket; recomputed on resolve / rating)
   ------------------------------------------------------------------------------ */
CREATE TABLE dbo.ServiceEvaluations
(
    EvaluationId       INT            NOT NULL IDENTITY(1,1) CONSTRAINT PK_ServiceEvaluations PRIMARY KEY,
    TicketId           INT            NOT NULL CONSTRAINT FK_ServiceEvaluations_Ticket REFERENCES dbo.Tickets(TicketId),
    SpeedScore         DECIMAL(5,2)   NOT NULL,
    QualityScore       DECIMAL(5,2)   NOT NULL,
    OverallScore       DECIMAL(5,2)   NOT NULL,
    Grade              TINYINT        NOT NULL,  -- 1 Excellent, 2 Good, 3 Fair, 4 Poor, 5 SlaBreached
    SlaBreached        BIT            NOT NULL,
    SatisfactionRating TINYINT        NULL,
    RequesterComment   NVARCHAR(1000) NULL,
    FormulaVersion     VARCHAR(10)    NOT NULL CONSTRAINT DF_ServiceEvaluations_Formula DEFAULT ('v1'),
    EvaluatedAt        DATETIME2(0)   NOT NULL CONSTRAINT DF_ServiceEvaluations_EvaluatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_ServiceEvaluations_Ticket UNIQUE (TicketId),
    CONSTRAINT CK_ServiceEvaluations_Scores CHECK (SpeedScore BETWEEN 0 AND 100 AND QualityScore BETWEEN 0 AND 100 AND OverallScore BETWEEN 0 AND 100),
    CONSTRAINT CK_ServiceEvaluations_Rating CHECK (SatisfactionRating IS NULL OR SatisfactionRating BETWEEN 1 AND 5),
    CONSTRAINT CK_ServiceEvaluations_Grade  CHECK (Grade BETWEEN 1 AND 5)
);
GO

/* ------------------------------------------------------------------------------
   6. REFERENCE DATA
   ------------------------------------------------------------------------------ */
INSERT dbo.Roles (RoleId, RoleName) VALUES (1, N'EndUser'), (2, N'ITAdministrator');

INSERT dbo.TicketStatuses (StatusId, StatusName, IsOpen) VALUES
 (1, N'Queued', 1), (2, N'Assigned', 1), (3, N'InProgress', 1), (4, N'Pending', 1),
 (5, N'Escalated', 1), (6, N'Resolved', 0), (7, N'Closed', 0);

-- ITIL-style default targets (minutes, wall-clock)
INSERT dbo.SlaPolicies (PriorityId, PriorityName, ResponseTargetMinutes, ResolutionTargetMinutes) VALUES
 (1, N'Critical',  15,  240),   -- respond 15 min, resolve 4 h
 (2, N'High',      30,  480),   -- respond 30 min, resolve 8 h
 (3, N'Medium',   120, 1440),   -- respond 2 h,   resolve 24 h
 (4, N'Low',      480, 4320);   -- respond 8 h,   resolve 72 h

INSERT dbo.TicketCategories (CategoryName, CategoryType, Description) VALUES
 (N'Hardware - Laptop / Desktop', 1, N'Physical device faults, peripherals, replacements'),
 (N'Hardware - Printer',          1, N'Printers, scanners, copiers'),
 (N'Software - Application',      2, N'Business application errors, installs, licences'),
 (N'Software - Email / Office',   2, N'Mail client, Office suite, calendar'),
 (N'Network - Connectivity',      3, N'LAN / Wi-Fi / VPN connectivity'),
 (N'Network - Access / Firewall', 3, N'Port, firewall and access requests'),
 (N'General - Account / Access',  4, N'Password resets, permissions, onboarding');

/* ------------------------------------------------------------------------------
   7. DEFAULT ADMIN (password: Admin@12345, PBKDF2-HMAC-SHA256, 600,000 iterations)
   ------------------------------------------------------------------------------ */
INSERT dbo.Users (RoleId, UserName, FullName, Email, Department, PasswordHash, PasswordSalt, HashIterations)
VALUES (2, N'admin', N'Default IT Administrator', N'it.admin@company.local', N'IT Operations',
        0xD070E0BABD07ACECA19A14E7240CF420A0DB4DA38A6DAB9F55858B1B991D2ED0,
        0xD0E35F7A8315CDEB248EC703562BB45E,
        600000);

/* ------------------------------------------------------------------------------
   8. SAMPLE REQUESTERS & TICKETS (relative to "now" so SLA states look realistic)
   ------------------------------------------------------------------------------ */
INSERT dbo.Users (RoleId, FullName, Email, Phone, Department) VALUES
 (1, N'Sara Ahmed',    N'sara.ahmed@company.local',   N'+20 100 000 0001', N'Finance'),
 (1, N'Omar Hassan',   N'omar.hassan@company.local',  N'+20 100 000 0002', N'Sales'),
 (1, N'Mona Khaled',   N'mona.khaled@company.local',  N'+20 100 000 0003', N'HR'),
 (1, N'Youssef Adel',  N'youssef.adel@company.local', N'+20 100 000 0004', N'Logistics');

DECLARE @now   DATETIME2(0) = SYSUTCDATETIME();
DECLARE @admin INT = (SELECT UserId FROM dbo.Users WHERE UserName = N'admin');
DECLARE @sara  INT = (SELECT UserId FROM dbo.Users WHERE Email = N'sara.ahmed@company.local');
DECLARE @omar  INT = (SELECT UserId FROM dbo.Users WHERE Email = N'omar.hassan@company.local');
DECLARE @mona  INT = (SELECT UserId FROM dbo.Users WHERE Email = N'mona.khaled@company.local');
DECLARE @yous  INT = (SELECT UserId FROM dbo.Users WHERE Email = N'youssef.adel@company.local');

/* Helper table: one row per sample ticket, offsets in minutes before @now */
DECLARE @s TABLE (Seq INT, RequesterId INT, CategoryId SMALLINT, PriorityId TINYINT, StatusId TINYINT,
                  Urgency TINYINT, Impact TINYINT, Subject NVARCHAR(200), Descr NVARCHAR(400),
                  AgeMin INT, FirstRespAfterMin INT NULL, ResolvedAfterMin INT NULL, ClosedAfterMin INT NULL,
                  Notes NVARCHAR(400) NULL, RootCause NVARCHAR(200) NULL,
                  AssetTag NVARCHAR(50) NULL, DeviceType NVARCHAR(60) NULL,
                  AppName NVARCHAR(100) NULL, AppVer NVARCHAR(40) NULL,
                  NetLoc NVARCHAR(100) NULL, Ip VARCHAR(45) NULL);

INSERT @s VALUES
 (1, @sara, 5, 1, 1, 1, 1, N'Whole finance floor lost network access', N'No wired or Wi-Fi connectivity on floor 3 since 08:00.', 10,  NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'Building A - Floor 3', '10.10.3.1'),
 (2, @omar, 3, 2, 3, 1, 2, N'CRM crashes when exporting quotes',         N'CRM client closes when exporting a quote to PDF.',       300, 20,   NULL, NULL, NULL, NULL, NULL, NULL, N'Contoso CRM', N'5.2.1', NULL, NULL),
 (3, @mona, 7, 3, 1, 2, 2, N'Password reset for HR portal',              N'Account locked after password expiry.',                  60,  NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
 (4, @yous, 2, 4, 4, 3, 2, N'Warehouse printer prints faded pages',      N'Toner possibly low on printer PRN-WH-02.',               900, 200,  NULL, NULL, NULL, NULL, N'PRN-WH-02', N'Laser Printer', NULL, NULL, NULL, NULL),
 (5, @sara, 1, 2, 6, 1, 2, N'Laptop does not boot',                      N'Black screen after BIOS logo.',                          2000,10,   300,  NULL, N'Replaced failed SSD and restored image.', N'SSD hardware failure', N'LT-FIN-0042', N'Laptop', NULL, NULL, NULL, NULL),
 (6, @omar, 4, 3, 7, 2, 2, N'Outlook calendar not syncing',              N'Meetings created on phone do not appear on desktop.',    4000,90,   1300, 1400, N'Rebuilt OST profile; sync restored.', N'Corrupted OST cache', NULL, NULL, N'Outlook', N'16.0', NULL, NULL),
 (7, @mona, 6, 4, 7, 3, 3, N'Open port 8443 for HR analytics tool',      N'Vendor tool requires outbound 8443.',                    9000,600,  5000, 5100, N'Firewall rule FW-221 added after security approval.', N'Missing firewall rule', NULL, NULL, NULL, NULL, N'DMZ', '10.20.0.15'),
 (8, @yous, 3, 1, 6, 1, 1, N'ERP posting service down',                  N'No transactions are posting to ERP.',                    3000,5,    200,  NULL, N'Restarted posting service and cleared stuck queue.', N'Message queue deadlock', NULL, NULL, N'ERP Posting Service', N'2024.3', NULL, NULL),
 (9, @sara, 5, 2, 5, 1, 2, N'VPN disconnects every 10 minutes',          N'Remote users report frequent VPN drops.',                700, 25,   NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'Remote / VPN', NULL);

INSERT dbo.Tickets (RequesterId, CategoryId, PriorityId, StatusId, Urgency, Impact, Subject, Description,
                    AssignedAdminId, CreatedAt, FirstResponseAt, ResponseDueAt, ResolutionDueAt,
                    ResolvedAt, ClosedAt, ResolutionNotes, RootCause,
                    AssetTag, DeviceType, ApplicationName, ApplicationVersion, NetworkLocation, AffectedIpAddress)
SELECT s.RequesterId, s.CategoryId, s.PriorityId, s.StatusId, s.Urgency, s.Impact, s.Subject, s.Descr,
       CASE WHEN s.FirstRespAfterMin IS NULL THEN NULL ELSE @admin END,
       DATEADD(MINUTE, -s.AgeMin, @now),
       CASE WHEN s.FirstRespAfterMin IS NULL THEN NULL ELSE DATEADD(MINUTE, s.FirstRespAfterMin - s.AgeMin, @now) END,
       DATEADD(MINUTE, p.ResponseTargetMinutes   - s.AgeMin, @now),
       DATEADD(MINUTE, p.ResolutionTargetMinutes - s.AgeMin, @now),
       CASE WHEN s.ResolvedAfterMin IS NULL THEN NULL ELSE DATEADD(MINUTE, s.ResolvedAfterMin - s.AgeMin, @now) END,
       CASE WHEN s.ClosedAfterMin   IS NULL THEN NULL ELSE DATEADD(MINUTE, s.ClosedAfterMin   - s.AgeMin, @now) END,
       s.Notes, s.RootCause, s.AssetTag, s.DeviceType, s.AppName, s.AppVer, s.NetLoc, s.Ip
FROM @s s JOIN dbo.SlaPolicies p ON p.PriorityId = s.PriorityId
ORDER BY s.Seq;

/* Audit trail for the samples (Created -> Assigned/InProgress -> Resolved -> Closed) */
INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, ToPriorityId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 1, NULL, 1, t.PriorityId, t.RequesterId, N'Ticket submitted', t.CreatedAt FROM dbo.Tickets t;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 2, 1, 2, @admin, N'Ticket received by IT', t.FirstResponseAt
FROM dbo.Tickets t WHERE t.FirstResponseAt IS NOT NULL;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 3, 2, 3, @admin, N'Work started', DATEADD(MINUTE, 1, t.FirstResponseAt)
FROM dbo.Tickets t WHERE t.FirstResponseAt IS NOT NULL AND t.StatusId <> 2;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 3, 3, 4, @admin, N'Waiting for toner delivery', DATEADD(MINUTE, 5, t.FirstResponseAt)
FROM dbo.Tickets t WHERE t.StatusId = 4;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 9, 3, 5, @admin, N'Escalated to network engineering (L2)', DATEADD(MINUTE, 5, t.FirstResponseAt)
FROM dbo.Tickets t WHERE t.StatusId = 5;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 6, 3, 6, @admin, t.ResolutionNotes, t.ResolvedAt
FROM dbo.Tickets t WHERE t.ResolvedAt IS NOT NULL;

INSERT dbo.TicketHistory (TicketId, ActionType, FromStatusId, ToStatusId, PerformedByUserId, Notes, OccurredAt)
SELECT t.TicketId, 7, 6, 7, @admin, N'Closed after requester confirmation', t.ClosedAt
FROM dbo.Tickets t WHERE t.ClosedAt IS NOT NULL;

/* Evaluations for resolved samples are computed by the application.
   Open the Statistics dashboard and press "Recalculate evaluations" once after setup. */
GO

/* ------------------------------------------------------------------------------
   9. OPTIONAL: least-privilege application login (uncomment & set a strong password)
   ------------------------------------------------------------------------------
USE master;
CREATE LOGIN servicedesk_app WITH PASSWORD = N'<StrongPassword!123>', CHECK_POLICY = ON;
USE ServiceDesk;
CREATE USER servicedesk_app FOR LOGIN servicedesk_app;
ALTER ROLE db_datareader ADD MEMBER servicedesk_app;
ALTER ROLE db_datawriter ADD MEMBER servicedesk_app;
-- Note: db_datawriter cannot bypass TR_TicketHistory_Immutable, so the audit log stays append-only.
   ------------------------------------------------------------------------------ */

PRINT N'ServiceDesk database created successfully.';
GO
