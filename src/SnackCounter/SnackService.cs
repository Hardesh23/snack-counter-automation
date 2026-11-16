namespace SnackCounter;

/// <summary>
/// Refill lifecycle, volunteer selection and budget check. Pure logic: chat and HR are
/// injected ports and the clock is injected. All mutation happens under one lock so every
/// state transition is atomic. In production the lock becomes a DB transaction plus a
/// unique partial index (one open refill per office).
/// </summary>
public class SnackService
{
    private static readonly RefillState[] OpenStates = { RefillState.Offered, RefillState.Committed };
    private static readonly RefillState[] SpendStates =
        { RefillState.AwaitingApproval, RefillState.Claimed, RefillState.Reimbursed };
    private static readonly RefillState[] PendingMoney = { RefillState.AwaitingApproval, RefillState.Claimed };

    private readonly Dictionary<string, Office> _offices;
    private readonly StaffDirectory _dir;
    private readonly IChatPort _chat;
    private readonly IHrPort _hr;
    private readonly Func<DateTime> _clock;
    private readonly IReadOnlyDictionary<string, decimal> _rates;   // finance-set, report only
    private readonly string _reportCurrency;
    private readonly Config _cfg;
    private readonly Dictionary<int, Refill> _refills = new();
    private readonly object _gate = new();
    private int _nextId;

    public SnackService(IEnumerable<Office> offices, StaffDirectory directory, IChatPort chat, IHrPort hr,
                        Func<DateTime> clock, IReadOnlyDictionary<string, decimal> ratesToReportCurrency,
                        string reportCurrency = "NOK", Config? config = null)
    {
        _offices = offices.ToDictionary(o => o.Name);
        _dir = directory;
        _chat = chat;
        _hr = hr;
        _clock = clock;
        _rates = ratesToReportCurrency;
        _reportCurrency = reportCurrency;
        _cfg = config ?? new Config();
    }

    public IReadOnlyCollection<Refill> Refills { get { lock (_gate) return _refills.Values.ToList(); } }

    private void Log(Refill r, string ev) => r.Log.Add((_clock(), ev));

    private Refill Get(int id) =>
        _refills.TryGetValue(id, out var r) ? r : throw new InvalidTransitionException($"unknown refill {id}");

    private static bool IsOpen(Refill r) => OpenStates.Contains(r.State);

    public Refill? OpenRefill(string office)
    {
        lock (_gate) return _refills.Values.FirstOrDefault(r => r.Office == office && IsOpen(r));
    }

    // ---- 1. empty -> offered
    /// <summary>Idempotent: at most one open refill per office, so nobody buys twice.</summary>
    public Refill ReportEmpty(string office)
    {
        lock (_gate)
        {
            var existing = _refills.Values.FirstOrDefault(r => r.Office == office && IsOpen(r));
            if (existing != null) return existing;
            var r = new Refill { Id = ++_nextId, Office = office, CreatedAt = _clock() };
            _refills[r.Id] = r;
            Log(r, "empty reported");
            OfferNext(r);
            return r;
        }
    }

    // ---- choosing who buys
    private string? ChooseVolunteer(string office, ISet<string> exclude)
    {
        var now = _clock();
        return _dir.Volunteers(office)
            .Where(e => !exclude.Contains(e)
                        && _dir.Location(e, now) == office        // visitors count where they are
                        && !_dir.IsAway(e, now))
            .Select(e =>
            {
                var mine = _refills.Values.Where(r => r.Buyer == e && r.PurchasedAt != null).ToList();
                return new
                {
                    Id = e,
                    Waiting = mine.Count(r => PendingMoney.Contains(r.State)),
                    Recent = mine.Count(r => now - r.PurchasedAt!.Value <= _cfg.FairnessWindow),
                    Last = mine.Select(r => r.PurchasedAt!.Value).DefaultIfEmpty(DateTime.MinValue).Max(),
                };
            })
            // 1) not already waiting for a reimbursement  2) fewest recent refills
            // 3) longest ago  4) id, for determinism
            .OrderBy(c => c.Waiting).ThenBy(c => c.Recent).ThenBy(c => c.Last)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => c.Id)
            .FirstOrDefault();
    }

    public string? ChooseVolunteer(string office, IEnumerable<string>? exclude = null)
    {
        lock (_gate) return ChooseVolunteer(office, new HashSet<string>(exclude ?? Array.Empty<string>()));
    }

    private void OfferNext(Refill r)
    {
        var who = ChooseVolunteer(r.Office, r.Excluded);
        var now = _clock();
        if (who == null)
        {
            r.Assignee = null;
            r.Deadline = null;
            r.State = RefillState.Offered;
            if (!r.Escalated)
            {
                r.Escalated = true;
                _chat.Send(_offices[r.Office].AdminId,
                           $"No volunteer available for the {r.Office} refill #{r.Id}.");
            }
            r.Excluded.Clear();   // next tick starts a new round
            Log(r, "no candidate; escalated");
            return;
        }
        r.State = RefillState.Offered;
        r.Assignee = who;
        r.Deadline = now + _cfg.OfferTtl;
        Log(r, $"offered to {who}");
        _chat.Send(who, $"The {r.Office} snack box is empty. Can you refill it? " +
                        $"Answer within {_cfg.OfferTtl}. (refill #{r.Id})");
    }

    // ---- 2. offered -> committed
    public Refill Accept(int refillId, string employeeId)
    {
        lock (_gate)
        {
            var r = Get(refillId);
            ExpireIfDue(r);
            if (r.State != RefillState.Offered || r.Assignee != employeeId)
                throw new InvalidTransitionException("this refill is not currently offered to you");
            r.State = RefillState.Committed;
            r.Buyer = employeeId;
            r.Deadline = _clock() + _cfg.PurchaseTtl;
            Log(r, $"{employeeId} committed");
            return r;
        }
    }

    /// <summary>Also used to back out after committing.</summary>
    public Refill Decline(int refillId, string employeeId)
    {
        lock (_gate)
        {
            var r = Get(refillId);
            if (r.Assignee != employeeId || !IsOpen(r))
                throw new InvalidTransitionException("not your open refill");
            Release(r, "declined");
            return r;
        }
    }

    private void Release(Refill r, string why)
    {
        var who = r.Assignee!;
        r.Excluded.Add(who);
        r.Buyer = null;
        Log(r, $"{who} {why}");
        OfferNext(r);
    }

    private void ExpireIfDue(Refill r)
    {
        if (IsOpen(r) && r.Assignee != null && r.Deadline != null && _clock() >= r.Deadline)
        {
            var was = r.State;
            _chat.Send(r.Assignee, $"Refill #{r.Id} was handed to someone else.");
            Release(r, was == RefillState.Offered ? "offer expired" : "did not buy in time");
        }
    }

    // ---- 3. committed -> bought (budget check) -> claimed
    public Refill SubmitPurchase(int refillId, string employeeId, decimal amount, byte[] receipt,
                                 IEnumerable<(string Name, int Qty)>? items = null)
    {
        lock (_gate)
        {
            var r = Get(refillId);
            if (r.State != RefillState.Committed || r.Buyer != employeeId)
                throw new InvalidTransitionException("only the committed buyer can submit a purchase");
            if (amount <= 0 || receipt == null || receipt.Length == 0)
                throw new SnackException("amount and receipt are required");

            var now = _clock();
            var office = _offices[r.Office];
            var spent = SpentUnlocked(r.Office, now.Year, now.Month);
            r.Amount = amount;
            r.PurchasedAt = now;
            r.Items = items?.ToList() ?? new();
            r.Receipt = receipt;

            if (spent + amount > office.MonthlyBudget)
            {
                r.State = RefillState.AwaitingApproval;
                Log(r, "over budget; awaiting approval");
                _chat.Send(office.AdminId,
                    $"Refill #{r.Id} ({amount} {office.Currency}) exceeds the monthly budget " +
                    $"({spent}/{office.MonthlyBudget}). Approve or reject.");
                _chat.Send(employeeId, "Thanks! This purchase is over budget and needs manager approval.");
            }
            else
            {
                FileClaim(r);
            }
            return r;
        }
    }

    private void FileClaim(Refill r)
    {
        var office = _offices[r.Office];
        // idempotency key = refill id: a retry can never create a second claim
        r.ClaimId = _hr.CreateClaim($"refill-{r.Id}", r.Buyer!, r.Amount!.Value, office.Currency,
                                    $"Snack counter refill {r.Office} #{r.Id}", r.Receipt!);
        r.State = RefillState.Claimed;
        r.ClaimedAt = _clock();
        Log(r, $"claim {r.ClaimId} filed");
        _chat.Send(r.Buyer!, $"Claim filed for {r.Amount} {office.Currency}. Thanks for refilling!");
    }

    public Refill ApproveOverBudget(int refillId, string approverId, bool approve = true)
    {
        lock (_gate)
        {
            var r = Get(refillId);
            if (r.State != RefillState.AwaitingApproval)
                throw new InvalidTransitionException("nothing to approve");
            if (approverId != _offices[r.Office].AdminId)
                throw new SnackException("only the office admin can approve");
            if (approve)
            {
                FileClaim(r);
            }
            else
            {
                r.State = RefillState.Rejected;
                _chat.Send(r.Buyer!, $"Refill #{r.Id} was not approved; the admin will contact you.");
            }
            return r;
        }
    }

    // ---- 4. claimed -> reimbursed, plus time-based rules
    /// <summary>Run periodically (e.g. every 5 min): hand off stale refills, sync HR.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            foreach (var r in _refills.Values.ToList())
            {
                if (!IsOpen(r)) continue;
                if (r.Assignee == null) OfferNext(r);
                else ExpireIfDue(r);
            }
            SyncClaims();
        }
    }

    public void SyncClaims()
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var r in _refills.Values.Where(r => r.State == RefillState.Claimed))
            {
                var status = _hr.GetStatus(r.ClaimId!);
                if (status == ClaimStatus.Approved)
                {
                    r.State = RefillState.Reimbursed;
                    Log(r, "reimbursed");
                    _chat.Send(r.Buyer!, $"Refill #{r.Id} reimbursed.");
                }
                else if (status == ClaimStatus.Rejected)
                {
                    r.State = RefillState.Rejected;
                    Log(r, "rejected by HR");
                    _chat.Send(r.Buyer!, $"HR rejected the claim for refill #{r.Id}.");
                    _chat.Send(_offices[r.Office].AdminId, $"HR rejected claim {r.ClaimId}.");
                }
                else if (now - r.ClaimedAt!.Value > _cfg.HrSla && !r.OverdueAlerted)
                {
                    r.OverdueAlerted = true;
                    _chat.Send(_offices[r.Office].AdminId,
                               $"Claim {r.ClaimId} is older than {_cfg.HrSla.Days} days; chase HR.");
                }
            }
        }
    }

    // ---- budget & reporting
    public decimal Spent(string office, int year, int month)
    {
        lock (_gate) return SpentUnlocked(office, year, month);
    }

    private decimal SpentUnlocked(string office, int year, int month) =>
        _refills.Values
            .Where(r => r.Office == office && SpendStates.Contains(r.State)
                        && r.PurchasedAt!.Value.Year == year && r.PurchasedAt.Value.Month == month)
            .Sum(r => r.Amount!.Value);

    public MonthlyReport MonthlyReport(int year, int month)
    {
        lock (_gate)
        {
            var offices = new Dictionary<string, OfficeReport>();
            decimal total = 0;
            foreach (var o in _offices.Values)
            {
                var rs = _refills.Values
                    .Where(r => r.Office == o.Name && SpendStates.Contains(r.State)
                                && r.PurchasedAt!.Value.Year == year && r.PurchasedAt.Value.Month == month)
                    .ToList();
                var spent = rs.Sum(r => r.Amount!.Value);
                var rate = o.Currency == _reportCurrency ? 1m : _rates[o.Currency];
                total += spent * rate;
                var top = rs.SelectMany(r => r.Items)
                    .GroupBy(i => i.Name)
                    .Select(g => (Name: g.Key, Qty: g.Sum(i => i.Qty)))
                    .OrderByDescending(i => i.Qty).ThenBy(i => i.Name, StringComparer.Ordinal)
                    .Take(5).ToList();
                offices[o.Name] = new OfficeReport(o.Currency, spent, o.MonthlyBudget,
                                                   o.MonthlyBudget - spent, rs.Count, top);
            }
            return new MonthlyReport(offices, Math.Round(total, 2), _reportCurrency);
        }
    }
}
