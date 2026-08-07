using MaterialLedger.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// TODO(Task 7): this is a temporary design-time-only registration so `dotnet ef` can
// resolve MaterialLedgerDbContext. Task 7 replaces this with the real registration.
builder.Services.AddDbContext<MaterialLedgerDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("MaterialLedger")
        ?? "Server=localhost;Database=MaterialLedger;Trusted_Connection=True;TrustServerCertificate=True;"));

var app = builder.Build();
app.Run();
