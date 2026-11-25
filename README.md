# Idempotent Stripe webhook in .NET 8
Companion code for the article. Run: `dotnet run --project src` with `Stripe__WebhookSecret=whsec_...`,
then `stripe listen --forward-to localhost:5000/stripe/webhook` and `stripe trigger invoice.paid`.
Send the same event twice (`stripe events resend evt_...`): PaidInvoices must stay at 1.
Tests: `dotnet test tests/Tests.csproj` (includes the replay test).

License: MIT, see [LICENSE](LICENSE). Copyright (c) 2026 Ricardo Ferreira.
