using SnackCounter;
using Xunit;

namespace SnackCounter.Tests;

public class Env
{
    public static readonly DateTime T0 = new(2026, 3, 10, 9, 0, 0);
    public static readonly byte[] Receipt = { 1, 2, 3 };

    public StaffDirectory Dir { get; } = new();
    public FakeClock Clock { get; } = new(T0);
    public FakeChat Chat { get; } = new();
    public FakeHr Hr { get; } = new();
    public SnackService Svc { get; }

    public Env(int oslo = 3, int blr = 3)
    {
        for (var i = 1; i <= oslo; i++)
            Dir.AddEmployee(new Employee($"o{i}", $"Oslo{i}", "Oslo", "NOK"), volunteer: true);
        for (var i = 1; i <= blr; i++)
            Dir.AddEmployee(new Employee($"b{i}", $"Blr{i}", "Bengaluru", "INR"), volunteer: true);
        var offices = new[]
        {
            new Office("Oslo", "NOK", 6000m, "admin_o"),
            new Office("Bengaluru", "INR", 15000m, "admin_b"),
        };
        Svc = new SnackService(offices, Dir, Chat, Hr, Clock.Read,
                               new Dictionary<string, decimal> { ["INR"] = 0.12m });
    }

    /// <summary>Run a refill through to purchase with whoever is offered.</summary>
    public Refill Buy(string office = "Oslo", decimal amount = 500m, (string, int)[]? items = null)
    {
        var r = Svc.ReportEmpty(office);
        Svc.Accept(r.Id, r.Assignee!);
        return Svc.SubmitPurchase(r.Id, r.Buyer!, amount, Receipt, items);
    }
}

public class LifecycleTests
{
    [Fact]
    public void HappyPath_EmptyToReimbursed()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        Assert.Equal(RefillState.Offered, r.State);
        e.Svc.Accept(r.Id, r.Assignee!);
        Assert.Equal(RefillState.Committed, r.State);
        e.Svc.SubmitPurchase(r.Id, r.Buyer!, 800m, Env.Receipt);
        Assert.Equal(RefillState.Claimed, r.State);

        var claim = e.Hr.Claims[r.ClaimId!];
        Assert.Equal(800m, claim.Amount);
        Assert.Equal("NOK", claim.Currency);
        Assert.Equal(r.Buyer, claim.EmployeeId);

        e.Hr.Approve(r.ClaimId!);
        e.Clock.Advance(TimeSpan.FromDays(3));
        e.Svc.Tick();
        Assert.Equal(RefillState.Reimbursed, r.State);
    }

    [Fact]
    public void DuplicateEmptyReports_CreateOneRefill()
    {
        var e = new Env();
        var a = e.Svc.ReportEmpty("Oslo");
        var b = e.Svc.ReportEmpty("Oslo");
        Assert.Same(a, b);
        Assert.Single(e.Svc.Refills);
        Assert.NotSame(a, e.Svc.ReportEmpty("Bengaluru"));   // other office is independent
    }

    [Fact]
    public void NewRefillPossibleOnceBoxIsRefilled()
    {
        var e = new Env();
        var first = e.Buy();
        var second = e.Svc.ReportEmpty("Oslo");
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void ConcurrentEmptyReports_CreateOneRefill()
    {
        var e = new Env();
        Parallel.For(0, 20, _ => e.Svc.ReportEmpty("Oslo"));
        Assert.Single(e.Svc.Refills);
    }
}

public class NoDoubleCommitTests
{
    [Fact]
    public void OnlyTheOfferedVolunteerCanCommit()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        var other = new[] { "o1", "o2", "o3" }.First(x => x != r.Assignee);
        Assert.Throws<InvalidTransitionException>(() => e.Svc.Accept(r.Id, other));
        e.Svc.Accept(r.Id, r.Assignee!);
        Assert.Throws<InvalidTransitionException>(() => e.Svc.Accept(r.Id, other));  // already committed
        Assert.Equal(r.Assignee, r.Buyer);
    }

    [Fact]
    public void SimultaneousAccepts_YieldExactlyOneCommit()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        var successes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var ids = Enumerable.Repeat(new[] { "o1", "o2", "o3" }, 5).SelectMany(x => x).ToArray();
        Parallel.ForEach(ids, emp =>
        {
            try { e.Svc.Accept(r.Id, emp); successes.Add(emp); }
            catch (InvalidTransitionException) { }
        });
        Assert.Equal(new[] { r.Assignee! }, successes.ToArray());
    }

    [Fact]
    public void OnlyBuyerCanSubmitReceipt()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        e.Svc.Accept(r.Id, r.Assignee!);
        var other = new[] { "o1", "o2", "o3" }.First(x => x != r.Buyer);
        Assert.Throws<InvalidTransitionException>(() => e.Svc.SubmitPurchase(r.Id, other, 500m, Env.Receipt));
    }
}

public class HandoffTests
{
    [Fact]
    public void UnansweredOffer_GoesToSomeoneElse()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        var first = r.Assignee!;
        e.Clock.Advance(TimeSpan.FromHours(5));
        e.Svc.Tick();
        Assert.NotEqual(first, r.Assignee);
        Assert.Equal(RefillState.Offered, r.State);
        Assert.Throws<InvalidTransitionException>(() => e.Svc.Accept(r.Id, first));  // late accept refused
    }

    [Fact]
    public void CommittedButNeverBought_IsReassigned()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        var first = r.Assignee!;
        e.Svc.Accept(r.Id, first);
        e.Clock.Advance(TimeSpan.FromHours(25));
        e.Svc.Tick();
        Assert.Equal(RefillState.Offered, r.State);
        Assert.NotEqual(first, r.Assignee);
        Assert.Null(r.Buyer);
        Assert.Contains(e.Chat.To(first), m => m.Contains("handed to someone else"));
    }

    [Fact]
    public void Decline_PassesOn_AndNobodyIsAskedTwiceInARound()
    {
        var e = new Env();
        var r = e.Svc.ReportEmpty("Oslo");
        var asked = new List<string> { r.Assignee! };
        for (var i = 0; i < 2; i++)
        {
            e.Svc.Decline(r.Id, r.Assignee!);
            asked.Add(r.Assignee!);
        }
        Assert.Equal(3, asked.Distinct().Count());
    }

    [Fact]
    public void EveryoneExhausted_EscalatesOnce_ThenRetries()
    {
        var e = new Env(oslo: 2);
        var r = e.Svc.ReportEmpty("Oslo");
        e.Svc.Decline(r.Id, r.Assignee!);
        e.Svc.Decline(r.Id, r.Assignee!);
        Assert.Null(r.Assignee);
        Assert.Single(e.Chat.To("admin_o"));
        e.Svc.Tick();                                   // new round begins
        Assert.NotNull(r.Assignee);
        Assert.Single(e.Chat.To("admin_o"));            // no spam
    }
}

public class ChoosingWhoBuysTests
{
    [Fact]
    public void PrefersFewestRecentRefills()
    {
        var e = new Env();
        foreach (var emp in new[] { "o1", "o2" })       // o1 and o2 refilled once; o3 never
        {
            var r = e.Svc.ReportEmpty("Oslo");
            while (r.Assignee != emp) e.Svc.Decline(r.Id, r.Assignee!);
            e.Svc.Accept(r.Id, emp);
            e.Svc.SubmitPurchase(r.Id, emp, 300m, Env.Receipt);
            e.Hr.Approve(r.ClaimId!);
            e.Svc.SyncClaims();
        }
        Assert.Equal("o3", e.Svc.ReportEmpty("Oslo").Assignee);
    }

    [Fact]
    public void AvoidsPeopleStillWaitingForReimbursement()
    {
        var e = new Env(oslo: 2);
        var waiting = e.Buy().Buyer;                    // buyer now waits for HR
        var r2 = e.Svc.ReportEmpty("Oslo");
        Assert.NotEqual(waiting, r2.Assignee);
    }

    [Fact]
    public void SkipsVolunteersWhoAreAway()
    {
        var e = new Env();
        e.Dir.AddAway("o1", Env.T0.AddDays(-1), Env.T0.AddDays(7));
        e.Dir.AddAway("o2", Env.T0.AddDays(-1), Env.T0.AddDays(7));
        Assert.Equal("o3", e.Svc.ReportEmpty("Oslo").Assignee);
    }

    [Fact]
    public void VisitorCountsWhereTheyAre()
    {
        var e = new Env(oslo: 1, blr: 1);
        // Bengaluru volunteer visits Oslo; the only Oslo volunteer is away.
        e.Dir.AddVisit("b1", "Oslo", Env.T0.AddDays(-1), Env.T0.AddDays(10));
        e.Dir.AddAway("o1", Env.T0.AddDays(-1), Env.T0.AddDays(7));
        // b1 is not on the Oslo roster, so Oslo has nobody and escalates ...
        Assert.Null(e.Svc.ReportEmpty("Oslo").Assignee);
        // ... and Bengaluru must not ask a person who is physically in Oslo.
        Assert.Null(e.Svc.ReportEmpty("Bengaluru").Assignee);
    }

    [Fact]
    public void VolunteerVisitingOtherOffice_NotAskedAtHomeOffice()
    {
        var e = new Env();
        e.Dir.AddVisit("o1", "Bengaluru", Env.T0.AddDays(-1), Env.T0.AddDays(10));
        var asked = new HashSet<string>();
        var r = e.Svc.ReportEmpty("Oslo");
        for (var i = 0; i < 2; i++)
        {
            asked.Add(r.Assignee!);
            e.Svc.Decline(r.Id, r.Assignee!);
        }
        Assert.DoesNotContain("o1", asked);
    }
}

public class BudgetTests
{
    [Fact]
    public void WithinBudget_FilesClaim()
    {
        var e = new Env();
        var r = e.Buy("Oslo", 1200m);
        Assert.Equal(RefillState.Claimed, r.State);
        Assert.Equal(1200m, e.Svc.Spent("Oslo", 2026, 3));
    }

    [Fact]
    public void OverBudget_IsHeld_AndNotSentToHr()
    {
        var e = new Env(oslo: 6);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1100m);   // 5500 spent
        var r = e.Buy("Oslo", 1000m);                        // 6500 > 6000
        Assert.Equal(RefillState.AwaitingApproval, r.State);
        Assert.Equal(5, e.Hr.Claims.Count);
        Assert.Contains(e.Chat.To("admin_o"), m => m.Contains("exceeds the monthly budget"));
    }

    [Fact]
    public void ExactlyOnBudget_IsAllowed()
    {
        var e = new Env(oslo: 6);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1200m);   // 6000
        Assert.Equal(6000m, e.Svc.Spent("Oslo", 2026, 3));
        Assert.Equal(5, e.Hr.Claims.Count);
        Assert.Equal(RefillState.AwaitingApproval, e.Buy("Oslo", 1m).State);
    }

    [Fact]
    public void ManagerApprovalFilesClaim_AndOnlyAdminMayApprove()
    {
        var e = new Env(oslo: 6);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1200m);
        var r = e.Buy("Oslo", 500m);
        Assert.Throws<SnackException>(() => e.Svc.ApproveOverBudget(r.Id, r.Buyer!));
        e.Svc.ApproveOverBudget(r.Id, "admin_o");
        Assert.Equal(RefillState.Claimed, r.State);
        Assert.Equal(6, e.Hr.Claims.Count);
    }

    [Fact]
    public void ManagerRejection_NotifiesVolunteer_AndIsNotCounted()
    {
        var e = new Env(oslo: 6);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1200m);
        var r = e.Buy("Oslo", 500m);
        e.Svc.ApproveOverBudget(r.Id, "admin_o", approve: false);
        Assert.Equal(RefillState.Rejected, r.State);
        Assert.Equal(6000m, e.Svc.Spent("Oslo", 2026, 3));
        Assert.Contains(e.Chat.To(r.Buyer!), m => m.Contains("not approved"));
    }

    [Fact]
    public void Budget_IsPerOfficeAndPerCurrency()
    {
        var e = new Env(oslo: 5, blr: 5);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1200m);   // Oslo full: 6000 NOK
        var r = e.Buy("Bengaluru", 3000m);                   // 3000 INR must not be compared to 6000 NOK
        Assert.Equal(RefillState.Claimed, r.State);
        Assert.Equal("INR", e.Hr.Claims[r.ClaimId!].Currency);
    }

    [Fact]
    public void Budget_ResetsNextMonth()
    {
        var e = new Env(oslo: 6);
        for (var i = 0; i < 5; i++) e.Buy("Oslo", 1200m);
        e.Clock.Now = new DateTime(2026, 4, 1, 9, 0, 0);
        Assert.Equal(RefillState.Claimed, e.Buy("Oslo", 1000m).State);
        Assert.Equal(6000m, e.Svc.Spent("Oslo", 2026, 3));
        Assert.Equal(1000m, e.Svc.Spent("Oslo", 2026, 4));
    }

    [Fact]
    public void VisitorBuyingInOtherOffice_UsesThatOfficesBudgetAndCurrency()
    {
        var e = new Env(oslo: 1, blr: 1);
        e.Dir.AddVisit("b1", "Oslo", Env.T0.AddDays(-1), Env.T0.AddDays(10));
        e.Dir.AddVolunteer("Oslo", "b1");                    // volunteers to help while visiting
        e.Dir.AddAway("o1", Env.T0.AddDays(-1), Env.T0.AddDays(7));
        var r = e.Buy("Oslo", 700m);
        Assert.Equal("b1", r.Buyer);
        Assert.Equal("NOK", e.Hr.Claims[r.ClaimId!].Currency);   // HR converts to payroll currency
        Assert.Equal(700m, e.Svc.Spent("Oslo", 2026, 3));
        Assert.Equal(0m, e.Svc.Spent("Bengaluru", 2026, 3));
    }
}

public class ReportTests
{
    [Fact]
    public void TotalsAcrossCurrencies()
    {
        var e = new Env();
        e.Buy("Oslo", 1000m, new[] { ("chips", 4) });
        e.Buy("Oslo", 500m, new[] { ("chips", 2), ("fruit", 3) });
        e.Buy("Bengaluru", 2500m, new[] { ("biscuits", 10) });
        var rep = e.Svc.MonthlyReport(2026, 3);
        Assert.Equal(1500m, rep.Offices["Oslo"].Spent);
        Assert.Equal(4500m, rep.Offices["Oslo"].Remaining);
        Assert.Equal(2500m, rep.Offices["Bengaluru"].Spent);
        Assert.Equal(12500m, rep.Offices["Bengaluru"].Remaining);
        Assert.Equal(1800.00m, rep.Total);                   // 1500 + 2500 * 0.12
        Assert.Equal("NOK", rep.TotalCurrency);
        Assert.Equal(("chips", 6), rep.Offices["Oslo"].TopItems[0]);
    }

    [Fact]
    public void ExcludesOtherMonthsAndUnfinishedRefills()
    {
        var e = new Env();
        e.Buy("Oslo", 1000m);
        e.Svc.ReportEmpty("Oslo");                           // open, nothing bought
        e.Clock.Now = new DateTime(2026, 4, 2);
        Assert.Equal(0m, e.Svc.MonthlyReport(2026, 4).Total);
        Assert.Equal(1000m, e.Svc.MonthlyReport(2026, 3).Total);
    }
}

public class ReimbursementTests
{
    [Fact]
    public void ClaimCreation_IsIdempotent()
    {
        var e = new Env();
        var r = e.Buy();
        var again = e.Hr.CreateClaim($"refill-{r.Id}", r.Buyer!, r.Amount!.Value, "NOK", "x", Env.Receipt);
        Assert.Equal(r.ClaimId, again);
        Assert.Single(e.Hr.Claims);
    }

    [Fact]
    public void HrRejection_AlertsAdmin_AndIsNotCounted()
    {
        var e = new Env();
        var r = e.Buy();
        e.Hr.Reject(r.ClaimId!);
        e.Svc.Tick();
        Assert.Equal(RefillState.Rejected, r.State);
        Assert.Contains(e.Chat.To("admin_o"), m => m.Contains("rejected"));
        Assert.Equal(0m, e.Svc.Spent("Oslo", 2026, 3));
    }

    [Fact]
    public void SlowClaim_AlertsAdminOnce()
    {
        var e = new Env();
        var r = e.Buy();
        e.Clock.Advance(TimeSpan.FromDays(11));
        e.Svc.Tick();
        e.Svc.Tick();
        Assert.Equal(1, e.Chat.To("admin_o").Count(m => m.Contains("chase HR")));
        Assert.Equal(RefillState.Claimed, r.State);
    }
}
