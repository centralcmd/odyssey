using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Context.Authorization;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class IdentityRoleConfiguration : IEntityTypeConfiguration<IdentityRole>
{
    public void Configure(EntityTypeBuilder<IdentityRole> entity)
    {
        entity.HasData(
            new IdentityRole
            {
                Id = RoleDefinitions.AdminId,
                Name = RoleDefinitions.Admin,
                NormalizedName = RoleDefinitions.Admin.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.AdminConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.OwnerId,
                Name = RoleDefinitions.Owner,
                NormalizedName = RoleDefinitions.Owner.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.OwnerConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.UserId,
                Name = RoleDefinitions.User,
                NormalizedName = RoleDefinitions.User.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.UserConcurrencyStamp
            },
            new IdentityRole
            {
                Id = RoleDefinitions.GuestId,
                Name = RoleDefinitions.Guest,
                NormalizedName = RoleDefinitions.Guest.ToLowerInvariant(),
                ConcurrencyStamp = RoleDefinitions.GuestConcurrencyStamp
            }
        );

        // Role CLAIMS are deliberately not seeded here. IdentityRoleClaim.Id is an int identity, so a
        // HasData seed has to assign ids positionally — and a counter running across all four role
        // lists means adding one claim renumbers every claim after it, scaffolding a migration full of
        // UpdateData/InsertData that renumbers unchanged rows. The repo's workaround was to hand-write
        // a raw-SQL migration per claim addition at fresh out-of-band ids and strip the renumbering out
        // of the scaffold, which left the model snapshot and every real database disagreeing on ids by
        // design. RoleClaimSeeder in Odyssey.MigrationService now reconciles the rows at runtime,
        // matching on (RoleId, ClaimType, ClaimValue) and letting the database assign ids, so adding a
        // claim needs no migration at all. Roles stay seeded here: their ids are fixed GUIDs.
    }
}
