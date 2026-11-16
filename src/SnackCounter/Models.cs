namespace SnackCounter;

// ---------------------------------------------------------------- ports
public interface IChatPort
{
    void Send(string userId, string text);
}

public enum ClaimStatus { Pending, Approved, Rejected }

public interface IHrPort
{
    /// <summary>Idempotent: the same key must always return the same claim id.</summary>
    string CreateClaim(string idempotencyKey, string employeeId, decimal amount,
                       string currency, string description, byte[] receipt);

    ClaimStatus GetStatus(string claimId);
}

// ---------------------------------------------------------------- model
public enum RefillState
{
    Offered,           // waiting for the one chosen volunteer (or unassigned)
    Committed,         // volunteer accepted, has a purchase deadline
    AwaitingApproval,  // bought, but over budget: office admin must approve
    Claimed,           // claim filed in HR, waiting for approval/payment
    Reimbursed,
    Rejected,          // rejected by admin or HR: needs human follow-up
    Cancelled,
}

public class SnackException : Exception
{
    public SnackException(string message) : base(message) { }
}

public class InvalidTransitionException : SnackException
{
    public InvalidTransitionException(string message) : base(message) { }
}

public record Office(string Name, string Currency, decimal MonthlyBudget, string AdminId);

public record Employee(string Id, string Name, string HomeOffice, string PayrollCurrency);

public record Config
{
    public TimeSpan OfferTtl { get; init; } = TimeSpan.FromHours(4);        // time to answer an offer
    public TimeSpan PurchaseTtl { get; init; } = TimeSpan.FromHours(24);    // time to buy after accepting
    public TimeSpan HrSla { get; init; } = TimeSpan.FromDays(10);           // HR approval takes 1-10 days
    public TimeSpan FairnessWindow { get; init; } = TimeSpan.FromDays(90);
}

public class Refill
{
    public int Id { get; init; }
    public required string Office { get; init; }
    public DateTime CreatedAt { get; init; }
    public RefillState State { get; internal set; } = RefillState.Offered;
    public string? Assignee { get; internal set; }       // currently asked volunteer
    public string? Buyer { get; internal set; }          // volunteer who accepted
    public DateTime? Deadline { get; internal set; }
    public HashSet<string> Excluded { get; } = new();    // declined/timed out for this refill
    public decimal? Amount { get; internal set; }
    public DateTime? PurchasedAt { get; internal set; }
    public List<(string Name, int Qty)> Items { get; internal set; } = new();
    public byte[]? Receipt { get; internal set; }
    public string? ClaimId { get; internal set; }
    public DateTime? ClaimedAt { get; internal set; }
    public bool Escalated { get; internal set; }
    public bool OverdueAlerted { get; internal set; }
    public List<(DateTime At, string Event)> Log { get; } = new();   // audit trail
}

/// <summary>Who is a volunteer where, who is visiting, who is away.</summary>
public class StaffDirectory
{
    private readonly Dictionary<string, Employee> _employees = new();
    private readonly Dictionary<string, HashSet<string>> _volunteers = new();
    private readonly List<(string Emp, string Office, DateTime Start, DateTime End)> _visits = new();
    private readonly List<(string Emp, DateTime Start, DateTime End)> _away = new();

    public void AddEmployee(Employee e, bool volunteer = false)
    {
        _employees[e.Id] = e;
        if (volunteer) AddVolunteer(e.HomeOffice, e.Id);
    }

    public void AddVolunteer(string office, string employeeId)
    {
        if (!_volunteers.TryGetValue(office, out var set))
            _volunteers[office] = set = new HashSet<string>();
        set.Add(employeeId);
    }

    public IReadOnlyCollection<string> Volunteers(string office) =>
        _volunteers.TryGetValue(office, out var set) ? set : Array.Empty<string>();

    public void AddVisit(string emp, string office, DateTime start, DateTime end) =>
        _visits.Add((emp, office, start, end));

    public void AddAway(string emp, DateTime start, DateTime end) => _away.Add((emp, start, end));

    /// <summary>Where the person physically is right now (visit overrides home office).</summary>
    public string Location(string emp, DateTime now)
    {
        foreach (var v in _visits)
            if (v.Emp == emp && v.Start <= now && now < v.End) return v.Office;
        return _employees[emp].HomeOffice;
    }

    public bool IsAway(string emp, DateTime now) =>
        _away.Any(a => a.Emp == emp && a.Start <= now && now < a.End);
}

// ---------------------------------------------------------------- report
public record OfficeReport(string Currency, decimal Spent, decimal Budget, decimal Remaining,
                           int Refills, IReadOnlyList<(string Name, int Qty)> TopItems);

public record MonthlyReport(IReadOnlyDictionary<string, OfficeReport> Offices,
                            decimal Total, string TotalCurrency);
