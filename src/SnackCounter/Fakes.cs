namespace SnackCounter;

/// <summary>Simulated clock, chat tool and HR system (stand-ins for the real systems).</summary>
public class FakeClock
{
    public DateTime Now { get; set; }
    public FakeClock(DateTime start) => Now = start;
    public void Advance(TimeSpan by) => Now += by;
    public DateTime Read() => Now;
}

public class FakeChat : IChatPort
{
    private readonly object _gate = new();
    private readonly List<(string User, string Text)> _sent = new();

    public IReadOnlyList<(string User, string Text)> Sent { get { lock (_gate) return _sent.ToList(); } }

    public void Send(string userId, string text)
    {
        lock (_gate) _sent.Add((userId, text));
    }

    public IReadOnlyList<string> To(string userId) =>
        Sent.Where(m => m.User == userId).Select(m => m.Text).ToList();
}

public class FakeHr : IHrPort
{
    public record Claim(string EmployeeId, decimal Amount, string Currency, string Description)
    {
        public ClaimStatus Status { get; set; } = ClaimStatus.Pending;
    }

    private readonly Dictionary<string, Claim> _claims = new();
    private readonly Dictionary<string, string> _keys = new();
    private readonly object _gate = new();

    public IReadOnlyDictionary<string, Claim> Claims { get { lock (_gate) return new Dictionary<string, Claim>(_claims); } }

    public string CreateClaim(string idempotencyKey, string employeeId, decimal amount, string currency,
                              string description, byte[] receipt)
    {
        if (receipt == null || receipt.Length == 0) throw new ArgumentException("HR requires a receipt image");
        lock (_gate)
        {
            if (_keys.TryGetValue(idempotencyKey, out var existing)) return existing;
            var id = $"HR-{_claims.Count + 1}";
            _claims[id] = new Claim(employeeId, amount, currency, description);
            _keys[idempotencyKey] = id;
            return id;
        }
    }

    public ClaimStatus GetStatus(string claimId)
    {
        lock (_gate) return _claims[claimId].Status;
    }

    // test controls: HR is run by another company, we only observe it
    public void Approve(string claimId) { lock (_gate) _claims[claimId].Status = ClaimStatus.Approved; }
    public void Reject(string claimId) { lock (_gate) _claims[claimId].Status = ClaimStatus.Rejected; }
}
