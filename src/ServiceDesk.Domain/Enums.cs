namespace ServiceDesk.Domain;

/// <summary>Values match dbo.Roles.</summary>
public enum UserRole : byte { EndUser = 1, ITAdministrator = 2 }

/// <summary>Values match dbo.TicketStatuses.</summary>
public enum TicketStatus : byte
{
    Queued = 1,
    Assigned = 2,
    InProgress = 3,
    Pending = 4,
    Escalated = 5,
    Resolved = 6,
    Closed = 7
}

/// <summary>Values match dbo.SlaPolicies.PriorityId (lower value = more important).</summary>
public enum TicketPriority : byte { Critical = 1, High = 2, Medium = 3, Low = 4 }

/// <summary>ITIL urgency: how fast the business needs it. 1 = High.</summary>
public enum Urgency : byte { High = 1, Medium = 2, Low = 3 }

/// <summary>ITIL impact: how many people / how much business is affected. 1 = High.</summary>
public enum Impact : byte { High = 1, Medium = 2, Low = 3 }

/// <summary>Values match dbo.TicketCategories.CategoryType; drives the ticket factory.</summary>
public enum TicketCategoryType : byte { Hardware = 1, Software = 2, Network = 3, General = 4 }

/// <summary>Values match dbo.TicketHistory.ActionType.</summary>
public enum HistoryAction : byte
{
    Created = 1,
    Assigned = 2,
    StatusChanged = 3,
    PriorityChanged = 4,
    NoteAdded = 5,
    Resolved = 6,
    Closed = 7,
    Reopened = 8,
    Escalated = 9,
    Rated = 10
}

/// <summary>Values match dbo.ServiceEvaluations.Grade.</summary>
public enum EvaluationGrade : byte { Excellent = 1, Good = 2, Fair = 3, Poor = 4, SlaBreached = 5 }

/// <summary>Live SLA state of a ticket (computed, not stored).</summary>
public enum SlaState { OnTrack, AtRisk, Breached, Met }

public static class EnumExtensions
{
    public static bool IsOpen(this TicketStatus status) =>
        status is not (TicketStatus.Resolved or TicketStatus.Closed);

    public static string ToDisplay(this TicketStatus status) => status switch
    {
        TicketStatus.InProgress => "In Progress",
        _ => status.ToString()
    };

    public static string ToDisplay(this EvaluationGrade grade) => grade switch
    {
        EvaluationGrade.SlaBreached => "SLA Breached",
        _ => grade.ToString()
    };

    public static string ToDisplay(this SlaState state) => state switch
    {
        SlaState.OnTrack => "On Track",
        SlaState.AtRisk => "At Risk",
        _ => state.ToString()
    };
}
