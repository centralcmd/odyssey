namespace Odyssey.Context.Authorization;

public static class RoleDefinitions
{
    public const string Admin = "Admin";
    public const string Owner = "Owner";
    public const string User = "User";
    public const string Guest = "Guest";

    public const string AdminId = "6c17017f-8072-44a8-8ed1-a03b71ef85a6";
    public const string UserId = "019ebf36-3a6a-43b2-aa58-7e022c3b9cf3";
    public const string OwnerId = "e444d6c0-1a33-4b2c-9f20-ef7a4ad2770b";
    public const string GuestId = "c9a82815-d9f8-4f3f-8b34-9f7272b71c7c";

    public const string AdminConcurrencyStamp = "c6c5b1d6-6c4a-4a5e-8c8b-7736ff6a8f27";
    public const string OwnerConcurrencyStamp = "5e6b9878-3b89-474f-a2db-0e3d1e38859d";
    public const string UserConcurrencyStamp = "1d63d0dc-6c9f-4ec4-90c5-2a7f8a0b2f2c";
    public const string GuestConcurrencyStamp = "27f1b3e3-82ff-4e4a-9e14-0c6a3c0f1f9e";
}
