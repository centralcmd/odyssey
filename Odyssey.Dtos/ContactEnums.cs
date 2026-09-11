namespace Odyssey.Dtos;

/// <summary>
/// A person contact's sex (issue #325 v5, §6). Optional — <c>null</c> means unspecified/not
/// provided. A deliberately minimal binary; no icon/registry treatment.
/// </summary>
public enum Sex
{
    Male = 1,
    Female = 2,
}

/// <summary>
/// The kind of postal address (issue #325 §6, extended for organizations by issue #47 §6).
///
/// <para>Ordinals <c>1</c>–<c>19</c> are the person/shared band and <c>20</c>+ the organization
/// band. The bands are <b>not</b> aligned across the three label enums and nothing should assume
/// they are — <see cref="ContactLabelScope"/>, never the ordinal, decides which members a given
/// <see cref="ContactType"/> may use.</para>
/// </summary>
public enum AddressLabel
{
    /// <summary>The person's residence.</summary>
    Home = 1,

    /// <summary>The person's workplace.</summary>
    Work = 2,

    /// <summary>Where invoices are sent (NO: <i>fakturaadresse</i>).</summary>
    Billing = 3,

    /// <summary>The fallback, the clamp target and the remap target. Valid for both contact types.</summary>
    Other = 4,

    /// <summary>A mailing-only address — P.O. box (NO: <i>postadresse</i>).</summary>
    Postal = 5,

    /// <summary>Where you physically turn up (NO: <i>besøksadresse</i>).</summary>
    Visiting = 20,

    /// <summary>The address in the business register (NO: <i>forretningsadresse</i>).</summary>
    Registered = 21,

    /// <summary>A named local office or branch, distinct from the head office.</summary>
    Branch = 22,
}

/// <summary>
/// The kind of email address (issue #325 v4, §6 — split from the earlier shared <c>ContactLabel</c>:
/// <c>Mobile</c> is a phone concept, not an email one, so email has its own dedicated enum).
/// Extended with the organization vocabulary by issue #47 §6; see <see cref="AddressLabel"/> for the
/// ordinal-band convention.
/// </summary>
public enum EmailLabel
{
    /// <summary>The person's private address. Displayed as "Personal" (issue #47 §9).</summary>
    Home = 1,

    /// <summary>The person's workplace address.</summary>
    Work = 2,

    /// <summary>The fallback, the clamp target and the remap target. Valid for both contact types.</summary>
    Other = 3,

    /// <summary>The catch-all published address (NO: <c>post@</c>, <c>firmapost@</c>).</summary>
    General = 20,

    /// <summary>Customer service (NO: <i>kundeservice</i>).</summary>
    Support = 21,

    /// <summary>New business, quotes, opening a product.</summary>
    Sales = 22,

    /// <summary>Invoicing and accounts (NO: <i>faktura</i>, EHF contact).</summary>
    Billing = 23,

    /// <summary>Insurance claims (NO: <i>skade</i>).</summary>
    Claims = 24,
}

/// <summary>
/// The kind of phone number (issue #325 v4, §6 — keeps <c>Mobile</c>; extended with the organization
/// vocabulary by issue #47 §6). See <see cref="AddressLabel"/> for the ordinal-band convention.
/// </summary>
public enum PhoneLabel
{
    /// <summary>Landline at the person's residence.</summary>
    Home = 1,

    /// <summary>The person's number at their workplace.</summary>
    Work = 2,

    /// <summary>A mobile number. Shared — a sole proprietor genuinely has one.</summary>
    Mobile = 3,

    /// <summary>The fallback, the clamp target and the remap target. Valid for both contact types.</summary>
    Other = 4,

    // Ordinal 5 is deliberately unused: `Fax` occupied it in issue #47 draft v1 and was dropped. A
    // future person/shared phone label should take it rather than extending the organization band.

    /// <summary>The main published number (NO: <i>sentralbord</i>).</summary>
    Switchboard = 20,

    /// <summary>Customer service.</summary>
    Support = 21,

    /// <summary>New business.</summary>
    Sales = 22,

    /// <summary>Invoicing and accounts.</summary>
    Billing = 23,

    /// <summary>Insurance claims.</summary>
    Claims = 24,

    /// <summary>A 24-hour line — card blocking, roadside assistance, emergency claims.</summary>
    Emergency = 25,

    /// <summary>A named individual's direct line inside the organization.</summary>
    Direct = 26,
}
