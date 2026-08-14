using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupplierIntegrationApi.Configuration;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public static class DevelopmentAdminSeeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AdminSeedOptions>>().Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            return;
        }

        var normalizer = scope.ServiceProvider.GetRequiredService<IEmailNormalizer>();
        var normalizedEmail = normalizer.Normalize(options.Email);
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await dbContext.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail))
        {
            return;
        }

        var user = new User
        {
            Email = options.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        user.PasswordHash = passwordHasher.HashPassword(user, options.Password);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }
}
