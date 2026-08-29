using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Creates the <c>AspNetUsers</c> rows that the domain rows in these tests are attributed to.
/// </summary>
/// <remarks>
/// Every <c>CreatedBy</c>/<c>UpdatedBy</c>/<c>AttachedBy</c>/<c>UploadedBy</c>/<c>RequestedBy</c>/
/// <c>ReviewedBy</c> column is a real foreign key to <c>AspNetUsers</c> now (see
/// <c>OdysseyContext</c>'s user-attribution keys), so a fixture that invents an id and writes it onto
/// a photo, a file or a journal entry is rejected by the engine rather than quietly stored.
///
/// <para>
/// That rejection is the point — it is the same constraint every production write goes through — so
/// the fix is to seed the principal, never to relax the key. Only the identity columns the key needs
/// are set; nothing here signs in.
/// </para>
/// </remarks>
internal static class AttributionUsers
{
    public static async Task EnsureAsync(OdysseyContext context, params string[] userIds)
    {
        var missing = userIds.Distinct(StringComparer.Ordinal).ToList();
        var present = await context.Users
            .Where(user => missing.Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync();

        foreach (var id in missing.Except(present, StringComparer.Ordinal))
        {
            var address = $"{id}@integration.test";
            context.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = address,
                NormalizedUserName = address.ToUpperInvariant(),
                Email = address,
                NormalizedEmail = address.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            });
        }

        await context.SaveChangesAsync();
    }
}
