# Snack Counter Automation (C# / .NET 10)

Automates refilling the snack counters in Oslo (NOK) and Bengaluru (INR): from "box is empty" to "volunteer reimbursed", with fair volunteer selection, double-buy prevention, budget checks and a monthly report.

- **Design document:** [DESIGN.md](DESIGN.md) / `DESIGN.docx`, with the lifecycle diagram in `docs/lifecycle.png` (`.svg` source)

## Run
Requires the .NET 10 SDK. No third-party packages in the library; tests use xUnit.

```bash
dotnet test                                   # 29 tests
dotnet run --project src/SnackCounter.Demo    # end-to-end walkthrough
```

## Layout
```
SnackCounter.slnx
src/SnackCounter/            class library
  Models.cs                  ports (IChatPort, IHrPort), states, Refill, StaffDirectory, report records
  SnackService.cs            lifecycle, choosing who buys, budget check, report
  Fakes.cs                   FakeChat, FakeHr, FakeClock (simulated stand-ins)
src/SnackCounter.Demo/       console demo
tests/SnackCounter.Tests/    xUnit tests
```

## Rules the tests prove
| Rule | Test class |
|---|---|
| One open refill per office; duplicate "empty" reports are ignored | `LifecycleTests` |
| Two people cannot both commit (incl. simultaneous threads) | `NoDoubleCommitTests` |
| Unanswered offer or missed purchase is handed to someone else; admin escalation when nobody is left | `HandoffTests` |
| Fair choice: fewest refills, not someone still waiting for money, skips away/visiting | `ChoosingWhoBuysTests` |
| Budget per office and currency, on-budget boundary, over-budget approval, month reset, visitors | `BudgetTests` |
| Monthly totals correct across NOK and INR | `ReportTests` |
| Idempotent HR claims, HR rejection, slow-claim alert | `ReimbursementTests` |

## Plugging in real systems
`IChatPort` and `IHrPort` are the only integration points:
- **Chat:** implement `Send(userId, text)`, and call `ReportEmpty`, `Accept`, `Decline` and `SubmitPurchase` from button/command handlers.
- **HR:** implement `CreateClaim(idempotencyKey, ...)` and `GetStatus(claimId)`.
- Call `Tick()` every few minutes (e.g. a `BackgroundService` with a timer).
- Replace the in-memory dictionary with a database; keep a unique partial index on open refills per office.
