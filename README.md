# IT Service Desk Management System (WPF + SQL Server)

A C# desktop application built with .NET 8 and WPF, backed by Microsoft SQL Server. It receives, prioritizes, assigns, tracks, and resolves support tickets, and includes statistics and service-quality evaluation.

- **Full design document** (architecture, ERD, algorithmic complexity, and formulas): [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- **Database script:** [`database/ServiceDesk_Setup.sql`](database/ServiceDesk_Setup.sql)

---

## 1. Requirements

| Component | Version |
|---|---|
| Operating system | Windows 10 / 11 (WPF and DPAPI are Windows-specific) |
| .NET SDK | 8.0 or later |
| Development environment | Visual Studio 2022 (17.8+) with the ".NET desktop development" workload, or `dotnet` CLI |
| SQL Server | 2016+ / Express / Developer / LocalDB / Azure SQL |
| Optional tools | SSMS, Azure Data Studio, or `sqlcmd` |

NuGet packages (restored automatically): `Microsoft.Data.SqlClient 5.2.2`, `System.Security.Cryptography.ProtectedData 8.0.0`, and `xunit` for the test project.

---

## 2. Setup and Run

### Step 1: Create the database

> The script drops the `ServiceDesk` database if it exists and recreates it.

**On the local machine (localhost):**
```bat
:: Default instance
sqlcmd -S localhost -E -C -i database\ServiceDesk_Setup.sql
:: SQL Express
sqlcmd -S .\SQLEXPRESS -E -C -i database\ServiceDesk_Setup.sql
:: LocalDB
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i database\ServiceDesk_Setup.sql
```

**On a remote server (IP address):**
```bat
sqlcmd -S 192.168.1.50,1433 -U sa -P "<password>" -C -i database\ServiceDesk_Setup.sql
```

Alternatively, open the script in SSMS and click **Execute**.

### Step 2: Prepare a remote server (IP connections only)

1. In **SQL Server Configuration Manager**, open **SQL Server Network Configuration**, enable **TCP/IP**, set port 1433 in IPAll, and restart the service.
2. Open port 1433 in the server firewall:
   ```powershell
   New-NetFirewallRule -DisplayName "SQL Server 1433" -Direction Inbound -Protocol TCP -LocalPort 1433 -Action Allow
   ```
3. For **SQL Authentication**, enable Mixed Mode in the server properties in SSMS. It is recommended to create a least-privilege user (see section 9 at the end of the script).
4. To use an instance name without a port, run the **SQL Server Browser** service and open UDP 1434.

### Step 3: Build and run

```bat
dotnet restore ServiceDesk.sln
dotnet build ServiceDesk.sln -c Release
dotnet run --project src\ServiceDesk.Wpf
dotnet test tests\ServiceDesk.Tests
```

Alternatively, open `ServiceDesk.sln` in Visual Studio, set `ServiceDesk.Wpf` as the startup project, and press F5.

### Step 4: First launch

1. The **Settings -> SQL Server connection** screen opens automatically when no saved settings exist.
2. Select **Localhost** (and its instance type) or **Remote IP address** (address and port), then choose the database name and authentication method.
3. Click **Test connection**. The message should show **Schema is ready**.
4. Click **Save & apply**. Settings are stored in `%AppData%\ServiceDesk\connection.json`, and the SQL password is encrypted with DPAPI.

> After saving, the settings are available only to the administrator account.

### Step 5: Sign in as administrator

| Field | Default value |
|---|---|
| Username | `admin` |
| Password | `Admin@12345` |

1. Change the password immediately: **Settings -> My password**.
2. Open **Statistics & evaluation** and click **Recalculate evaluations** once to evaluate the sample resolved tickets.

---

## 3. Quick Usage Guide

### End-user mode (no password)
- **Submit a ticket:** Enter personal details and a category (hardware, software, or network fields appear as applicable), choose urgency and impact, and preview the resulting priority immediately. The user receives a number such as `TKT-0000010`.
- **Track my ticket:** Enter both the ticket number and email. View the status, SLA, public updates, resolution notes, and submit a satisfaction rating (1-5) after resolution.

### IT administrator mode (password required)
- **Queue & triage:** View a queue ordered according to the selected strategy, filter by status, priority, category, search, or my tickets, and highlight overdue or at-risk tickets. The queue refreshes every 30 seconds.
  - **Receive next ticket:** Assigns the highest-priority unassigned ticket to you.
  - Actions: Claim, Start work, Pending, Escalate, Return to queue, Change priority, Add note (internal or public), Resolve & evaluate, Close, and Reopen. Buttons that are not allowed for the current status are disabled automatically.
  - Each ticket has a complete, immutable audit log.
- **Statistics & evaluation:** Review performance metrics, resolution time (average, median, minimum, maximum, P90, and standard deviation), SLA compliance, distributions, individual ticket evaluations, and CSV/JSON exports.
- **Settings:** Configure SQL connections and editable SLA policy rules, and change the password.

---

## 4. Solution Structure

```
ServiceDesk.sln
├── database/ServiceDesk_Setup.sql        DDL, indexes, update-prevention trigger, and seed data
├── docs/ARCHITECTURE.md                  Design document
├── src/
│   ├── ServiceDesk.Domain/               Entities, rules, strategies, factory, and algorithms
│   ├── ServiceDesk.Application/          Services, abstractions, hashing, statistics, and export
│   ├── ServiceDesk.Data/                 ADO.NET, repositories, unit of work, and settings (Singleton)
│   └── ServiceDesk.Wpf/                  Views, view models, converters, styles, and composition root
└── tests/ServiceDesk.Tests/              xUnit tests for domain logic, algorithms, and formulas
```

---

## 5. Planned Features

| Feature | Value | Implementation note |
|---|---|---|
| Pause the SLA clock while in `Pending`; add a business-hours calendar and holidays | More accurate measurement | `BusinessCalendars` table and net-duration calculation |
| Email notifications (SMTP / Microsoft Graph) | Notify users without opening the application | Outbox pattern for reliable delivery |
| Attachments (screenshots and logs) | Faster diagnosis | `VARBINARY(MAX)`, FILESTREAM, or external storage |
| Multiple support teams with category-based automatic routing | Balanced workload | `Teams` and `CategoryRouting` tables |
| Known-error knowledge base | Faster self-service and fewer tickets | Link `RootCause` to articles |
| Full-text search | O(log N) search instead of a table scan | `CREATE FULLTEXT INDEX` |
| Event logging (Serilog) and a DI container | Easier operation and monitoring | `Microsoft.Extensions.Hosting` |
| Full Arabic RTL interface | Improved usability for Arabic users | `.resx` files and `FlowDirection="RightToLeft"` |
| Database-backed account lockout instead of in-memory state | Protection that survives application restarts | `FailedLogins` and `LockoutUntil` columns |

---

## 6. Troubleshooting

| Symptom | Likely cause | Solution |
|---|---|---|
| Error 53 / Server not reachable | TCP/IP disabled or firewall blocked | Complete Step 2 |
| Error 18456 / Login failed | Invalid credentials or Mixed Mode disabled | Enable Mixed Mode or use Windows Authentication |
| Certificate error (certificate chain) | `Encrypt=Mandatory` with a self-signed certificate | Enable "Trust server certificate" for development |
| "ServiceDesk tables were not found" | The script was not run against the selected database | Complete Step 1 |
| "Modified by another administrator" | Intentional concurrency conflict | Click Refresh and retry |
