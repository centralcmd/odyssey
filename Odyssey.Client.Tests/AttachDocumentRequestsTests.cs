using Odyssey.Client.Pages.Attachments;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Each finance surface's half of the shared "Attach documents" dialog: its name-based type guess and
/// its item → attach-request mapping. A wrong fallback posts a type the server stores as a different
/// member, and a dropped validity field clears a date the reader entered — neither looks wrong on screen.
/// </summary>
public class AttachDocumentRequestsTests
{
    private static readonly Guid FileId = Guid.NewGuid();
    private static readonly Guid Issuer = Guid.NewGuid();

    private static AttachDocumentItem Item(string kind) => new(
        AttachDocumentSource.Library, FileId, "doc.pdf", kind, 10,
        new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), new DateTime(2025, 12, 1), Issuer);

    [Theory]
    [InlineData("statement.pdf", "Statement")]
    [InlineData("STATEMENT.PDF", "Statement")]
    [InlineData("receipt.jpg", "Other")]
    public void Account_guess(string name, string expected) => Assert.Equal(expected, AttachDocumentRequests.AccountGuess(name));

    [Theory]
    [InlineData("skatteoppgjør-2025.pdf", "TaxAssessment")]
    [InlineData("assessment-notice.jpg", "TaxAssessment")]
    [InlineData("return.pdf", "TaxReturn")]
    [InlineData("payslip.png", "SupportingDocument")]
    public void Tax_guess(string name, string expected) => Assert.Equal(expected, AttachDocumentRequests.TaxGuess(name));

    [Theory]
    [InlineData("kvittering.jpg", "Receipt")]
    [InlineData("scan.HEIC", "Receipt")]
    [InlineData("invoice.pdf", "Invoice")]
    [InlineData("export.csv", "Other")]
    public void Transaction_guess(string name, string expected) => Assert.Equal(expected, AttachDocumentRequests.TransactionGuess(name));

    [Fact]
    public void Account_request_carries_the_type_and_all_four_validity_fields()
    {
        var r = AttachDocumentRequests.Account(Item(nameof(AccountFileType.Statement)));

        Assert.Equal(new AttachAccountFileRequest
        {
            FileId = FileId,
            FileType = AccountFileType.Statement,
            ValidFrom = new DateTime(2026, 1, 1),
            ValidTo = new DateTime(2027, 1, 1),
            IssuedAt = new DateTime(2025, 12, 1),
            IssuedBy = Issuer,
        }, r);
    }

    [Fact]
    public void Contract_request_carries_the_type_and_validity_and_falls_back_to_signed()
    {
        var r = AttachDocumentRequests.Contract(Item(nameof(ContractFileType.Amendment)));

        Assert.Equal(FileId, r.FileMetadataId);
        Assert.Equal(ContractFileType.Amendment, r.FileType);
        Assert.Equal(new DateTime(2026, 1, 1), r.ValidFrom);
        Assert.Equal(new DateTime(2027, 1, 1), r.ValidTo);
        Assert.Equal(new DateTime(2025, 12, 1), r.IssuedAt);
        Assert.Equal(Issuer, r.IssuedBy);
        Assert.Equal(ContractFileType.Signed, AttachDocumentRequests.Contract(Item("Nonsense")).FileType);
    }

    [Fact]
    public void Property_request_carries_the_type_and_validity_and_falls_back_to_other()
    {
        var r = AttachDocumentRequests.Property(Item(nameof(PropertyFileType.Deed)));

        Assert.Equal(FileId, r.FileMetadataId);
        Assert.Equal(PropertyFileType.Deed, r.FileType);
        Assert.Equal(Issuer, r.IssuedBy);
        Assert.Equal(PropertyFileType.Other, AttachDocumentRequests.Property(Item("Nonsense")).FileType);
    }

    [Fact]
    public void Tax_and_transaction_requests_carry_the_type_and_fall_back_to_other()
    {
        Assert.Equal(new AttachTaxStatementFileRequest { FileId = FileId, FileType = TaxStatementFileType.TaxReturn },
            AttachDocumentRequests.TaxStatement(Item(nameof(TaxStatementFileType.TaxReturn))));
        Assert.Equal(TaxStatementFileType.Other, AttachDocumentRequests.TaxStatement(Item("Nonsense")).FileType);
        Assert.Equal(TransactionFileType.Receipt, AttachDocumentRequests.TransactionType(Item(nameof(TransactionFileType.Receipt))));
        Assert.Equal(TransactionFileType.Other, AttachDocumentRequests.TransactionType(Item("Nonsense")));
    }

    /// <summary>
    /// The account and tax surfaces post no validity: the tax request has no such fields, and the account
    /// dialog passes Validity=true — so a null there is the reader's blank, never a dropped value.
    /// </summary>
    [Fact]
    public void An_item_without_validity_posts_nulls()
    {
        var bare = new AttachDocumentItem(AttachDocumentSource.Upload, FileId, "doc.pdf", "Other", 1, null, null, null, null);

        var r = AttachDocumentRequests.Account(bare);

        Assert.Null(r.ValidFrom);
        Assert.Null(r.ValidTo);
        Assert.Null(r.IssuedAt);
        Assert.Null(r.IssuedBy);
    }
}
