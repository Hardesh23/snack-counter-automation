using SnackCounter;

// End-to-end demo with the simulated chat tool and HR system.
var dir = new StaffDirectory();
for (var i = 1; i <= 3; i++)
{
    dir.AddEmployee(new Employee($"o{i}", $"Oslo{i}", "Oslo", "NOK"), volunteer: true);
    dir.AddEmployee(new Employee($"b{i}", $"Blr{i}", "Bengaluru", "INR"), volunteer: true);
}
var offices = new[]
{
    new Office("Oslo", "NOK", 6000m, "admin_o"),
    new Office("Bengaluru", "INR", 15000m, "admin_b"),
};
var clock = new FakeClock(new DateTime(2026, 3, 10, 9, 0, 0));
var chat = new FakeChat();
var hr = new FakeHr();
var svc = new SnackService(offices, dir, chat, hr, clock.Read,
                           new Dictionary<string, decimal> { ["INR"] = 0.12m });
var receipt = new byte[] { 1 };

Console.WriteLine("1. Oslo box is empty -> first volunteer is asked");
var r = svc.ReportEmpty("Oslo");
Console.WriteLine($"   offered to {r.Assignee}");

Console.WriteLine("2. They don't answer for 5 hours -> handed to the next person");
clock.Advance(TimeSpan.FromHours(5));
svc.Tick();
Console.WriteLine($"   offered to {r.Assignee}");

Console.WriteLine("3. They accept, buy, and send the receipt");
svc.Accept(r.Id, r.Assignee!);
svc.SubmitPurchase(r.Id, r.Buyer!, 900m, receipt, new[] { ("chips", 5), ("fruit", 4) });
var c = hr.Claims[r.ClaimId!];
Console.WriteLine($"   state: {r.State} | HR claim {r.ClaimId}: {c.Amount} {c.Currency} for {c.EmployeeId}");

Console.WriteLine("4. Bengaluru refill too");
var b = svc.ReportEmpty("Bengaluru");
svc.Accept(b.Id, b.Assignee!);
svc.SubmitPurchase(b.Id, b.Buyer!, 2500m, receipt, new[] { ("biscuits", 10) });

Console.WriteLine("5. HR approves after 3 days -> reimbursed");
clock.Advance(TimeSpan.FromDays(3));
hr.Approve(r.ClaimId!);
svc.Tick();
Console.WriteLine($"   state: {r.State}");

Console.WriteLine("6. Monthly management report");
var rep = svc.MonthlyReport(2026, 3);
foreach (var (name, o) in rep.Offices)
{
    var top = string.Join(", ", o.TopItems.Select(t => $"{t.Name} x{t.Qty}"));
    Console.WriteLine($"   {name}: {o.Spent}/{o.Budget} {o.Currency} ({o.Refills} refills) top items: {top}");
}
Console.WriteLine($"   TOTAL: {rep.Total:0.00} {rep.TotalCurrency}");

Console.WriteLine("\nChat messages sent:");
foreach (var (user, text) in chat.Sent) Console.WriteLine($"   -> {user}: {text}");
