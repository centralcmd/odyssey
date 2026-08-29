namespace Odyssey.Dtos;

/// <summary>
/// A person contact's relationship to the user (issue #325 §6). Optional — <c>null</c> means
/// unspecified.
/// </summary>
public enum RelationshipType
{
    Family = 1,
    Landlord = 2,
    Employer = 3,
    Other = 4,
}

/// <summary>
/// A person contact's sex (issue #325 v5, §6). Optional — <c>null</c> means unspecified/not
/// provided. A deliberately minimal binary; no icon/registry treatment.
/// </summary>
public enum Sex
{
    Male = 1,
    Female = 2,
}

/// <summary>The kind of postal address (issue #325 §6).</summary>
public enum AddressLabel
{
    Home = 1,
    Work = 2,
    Billing = 3,
    Other = 4,
}

/// <summary>
/// The kind of email address (issue #325 v4, §6 — split from the earlier shared <c>ContactLabel</c>:
/// <c>Mobile</c> is a phone concept, not an email one, so email has its own dedicated enum).
/// </summary>
public enum EmailLabel
{
    Home = 1,
    Work = 2,
    Other = 3,
}

/// <summary>The kind of phone number (issue #325 v4, §6 — keeps <c>Mobile</c>).</summary>
public enum PhoneLabel
{
    Home = 1,
    Work = 2,
    Mobile = 3,
    Other = 4,
}
