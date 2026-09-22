using Odyssey.Dtos;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;
using ContextTransactionFileType = Odyssey.Context.TransactionFileType;
using ContextTaxStatementFileType = Odyssey.Context.TaxStatementFileType;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoBudgetCategoryType = Odyssey.Dtos.Finance.BudgetCategoryType;
using DtoTransactionFileType = Odyssey.Dtos.Finance.TransactionFileType;
using DtoTaxStatementFileType = Odyssey.Dtos.Finance.TaxStatementFileType;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextInterval = Odyssey.Context.Interval;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using ContextBudgetItem = Odyssey.Context.BudgetItem;
using DtoExistingBudgetItem = Odyssey.Dtos.Finance.ExistingBudgetItem;

namespace Odyssey.Core.Finance;

public static class MapsterConfig
{
    private static readonly object SyncRoot = new();
    private static bool configured;

    // Runs once when the Odyssey.Core.Finance assembly is loaded — before any service, controller,
    // seeder, or test constructs a type from it — so the global Mapster config is registered a
    // single time per process instead of on every service-constructor call.
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Deliberate: registering the Mapster config once on assembly load is the point — "
            + "it is what keeps every service constructor from re-registering it.")]
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static void Register()
    {
        if (configured)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (configured)
            {
                return;
            }

            TypeAdapterConfig<ContextAccountType, DtoAccountType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoAccountType, ContextAccountType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextAccountFileType, DtoAccountFileType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoAccountFileType, ContextAccountFileType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextBudgetCategoryType, DtoBudgetCategoryType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoBudgetCategoryType, ContextBudgetCategoryType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextTransactionFileType, DtoTransactionFileType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoTransactionFileType, ContextTransactionFileType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextTaxStatementFileType, DtoTaxStatementFileType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoTaxStatementFileType, ContextTaxStatementFileType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextTermKind, DtoTermKind>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoTermKind, ContextTermKind>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextTermValueUnit, DtoTermValueUnit>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoTermValueUnit, ContextTermValueUnit>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            // HEADS-UP (Mapster version): this MapWith converter is registered for the NON-nullable
            // Interval pair, but Term.Interval is nullable and maps to the (different) nullable
            // Dtos.Interval. Mapster 10.0.8 lifts this converter over Nullable<T> with a
            // null guard (null -> null); Mapster 10.0.9 regressed that lifting and calls src.Value
            // unconditionally, throwing "Nullable object must have a value" for null intervals
            // (interest-rate/expected-return terms legitimately have none). That broke 17 term
            // tests, so Mapster is pinned to 10.0.8 in Directory.Packages.props. Before accepting a bump
            // to >= 10.0.9, register null-guarded nullable converters here, e.g.
            //   TypeAdapterConfig<ContextInterval?, DtoInterval?>.NewConfig()
            //       .MapWith(src => src.HasValue ? ConvertContextToDto(src.Value) : null);
            // (and the reverse), then re-verify the term suites stay green.
            TypeAdapterConfig<ContextInterval, DtoInterval>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoInterval, ContextInterval>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            // A budget item's identity IS its tag (issue #75), so the read model embeds it — and this
            // registration is what puts it there. Mapster maps by NAME convention otherwise, and
            // BudgetItem.TransactionTag does not match ExistingBudgetItem.Tag, so without this line
            // the property is silently left unset: `required` does not catch it either, because
            // Mapster constructs through a runtime Expression.MemberInit which bypasses
            // required-member enforcement. The whole design would degrade to a null tag with no
            // compiler or test failure — hence the explicit mapping and the test that pins it.
            TypeAdapterConfig<ContextBudgetItem, DtoExistingBudgetItem>
                .NewConfig()
                .Map(dest => dest.Tag, src => src.TransactionTag);

            // (The former Account→ExistingAccount Ignore(Custodian) pin was removed with the Contact
            // move: Account no longer has a Custodian navigation — only the scalar CustodianId — so there
            // is nothing for Mapster to auto-map onto the slim Custodian DTO. The service resolves
            // ExistingAccount.Custodian explicitly via IContactLookup. Contact read-projection Mapster
            // config moved to Odyssey.Core.Journal/ContactMapsterConfig.cs with the aggregate (issue #325).)

            configured = true;
        }
    }

    private static DtoAccountType ConvertContextToDto(ContextAccountType src)
    {
        return src switch
        {
            // Assets
            ContextAccountType.Cash => DtoAccountType.Cash,
            ContextAccountType.CheckingAccount => DtoAccountType.CheckingAccount,
            ContextAccountType.SavingsAccount => DtoAccountType.SavingsAccount,
            ContextAccountType.InvestmentAccount => DtoAccountType.InvestmentAccount,
            ContextAccountType.PensionAccount => DtoAccountType.PensionAccount,
            ContextAccountType.Property => DtoAccountType.Property,
            ContextAccountType.Vehicle => DtoAccountType.Vehicle,
            ContextAccountType.OtherAsset => DtoAccountType.OtherAsset,
            // Liabilities
            ContextAccountType.CreditCard => DtoAccountType.CreditCard,
            ContextAccountType.Mortgage => DtoAccountType.Mortgage,
            ContextAccountType.StudentLoan => DtoAccountType.StudentLoan,
            ContextAccountType.PersonalLoan => DtoAccountType.PersonalLoan,
            ContextAccountType.CarLoan => DtoAccountType.CarLoan,
            ContextAccountType.TaxDebt => DtoAccountType.TaxDebt,
            ContextAccountType.OtherLiability => DtoAccountType.OtherLiability,
            _ => DtoAccountType.Unknown,
        };
    }

    private static ContextAccountType ConvertDtoToContext(DtoAccountType src)
    {
        return src switch
        {
            // Assets
            DtoAccountType.Cash => ContextAccountType.Cash,
            DtoAccountType.CheckingAccount => ContextAccountType.CheckingAccount,
            DtoAccountType.SavingsAccount => ContextAccountType.SavingsAccount,
            DtoAccountType.InvestmentAccount => ContextAccountType.InvestmentAccount,
            DtoAccountType.PensionAccount => ContextAccountType.PensionAccount,
            DtoAccountType.Property => ContextAccountType.Property,
            DtoAccountType.Vehicle => ContextAccountType.Vehicle,
            DtoAccountType.OtherAsset => ContextAccountType.OtherAsset,
            // Liabilities
            DtoAccountType.CreditCard => ContextAccountType.CreditCard,
            DtoAccountType.Mortgage => ContextAccountType.Mortgage,
            DtoAccountType.StudentLoan => ContextAccountType.StudentLoan,
            DtoAccountType.PersonalLoan => ContextAccountType.PersonalLoan,
            DtoAccountType.CarLoan => ContextAccountType.CarLoan,
            DtoAccountType.TaxDebt => ContextAccountType.TaxDebt,
            DtoAccountType.OtherLiability => ContextAccountType.OtherLiability,
            _ => ContextAccountType.Unknown,
        };
    }

    private static DtoAccountFileType ConvertContextToDto(ContextAccountFileType src) => src switch
    {
        ContextAccountFileType.Message => DtoAccountFileType.Message,
        ContextAccountFileType.Statement => DtoAccountFileType.Statement,
        ContextAccountFileType.Contract => DtoAccountFileType.Contract,
        ContextAccountFileType.Tax => DtoAccountFileType.Tax,
        ContextAccountFileType.Documentation => DtoAccountFileType.Documentation,
        ContextAccountFileType.InsurancePolicy => DtoAccountFileType.InsurancePolicy,
        ContextAccountFileType.LoanAgreement => DtoAccountFileType.LoanAgreement,
        ContextAccountFileType.RepaymentSchedule => DtoAccountFileType.RepaymentSchedule,
        ContextAccountFileType.PurchaseAgreement => DtoAccountFileType.PurchaseAgreement,
        ContextAccountFileType.Valuation => DtoAccountFileType.Valuation,
        ContextAccountFileType.Warranty => DtoAccountFileType.Warranty,
        ContextAccountFileType.Registration => DtoAccountFileType.Registration,
        ContextAccountFileType.Prospectus => DtoAccountFileType.Prospectus,
        _ => DtoAccountFileType.Other,
    };

    private static ContextAccountFileType ConvertDtoToContext(DtoAccountFileType src) => src switch
    {
        DtoAccountFileType.Message => ContextAccountFileType.Message,
        DtoAccountFileType.Statement => ContextAccountFileType.Statement,
        DtoAccountFileType.Contract => ContextAccountFileType.Contract,
        DtoAccountFileType.Tax => ContextAccountFileType.Tax,
        DtoAccountFileType.Documentation => ContextAccountFileType.Documentation,
        DtoAccountFileType.InsurancePolicy => ContextAccountFileType.InsurancePolicy,
        DtoAccountFileType.LoanAgreement => ContextAccountFileType.LoanAgreement,
        DtoAccountFileType.RepaymentSchedule => ContextAccountFileType.RepaymentSchedule,
        DtoAccountFileType.PurchaseAgreement => ContextAccountFileType.PurchaseAgreement,
        DtoAccountFileType.Valuation => ContextAccountFileType.Valuation,
        DtoAccountFileType.Warranty => ContextAccountFileType.Warranty,
        DtoAccountFileType.Registration => ContextAccountFileType.Registration,
        DtoAccountFileType.Prospectus => ContextAccountFileType.Prospectus,
        _ => ContextAccountFileType.Other,
    };

    private static DtoBudgetCategoryType ConvertContextToDto(ContextBudgetCategoryType src)
    {
        return src switch
        {
            ContextBudgetCategoryType.Expense => DtoBudgetCategoryType.Expense,
            ContextBudgetCategoryType.Income => DtoBudgetCategoryType.Income,
            _ => DtoBudgetCategoryType.Expense,
        };
    }

    private static ContextBudgetCategoryType ConvertDtoToContext(DtoBudgetCategoryType src)
    {
        return src switch
        {
            DtoBudgetCategoryType.Expense => ContextBudgetCategoryType.Expense,
            DtoBudgetCategoryType.Income => ContextBudgetCategoryType.Income,
            _ => ContextBudgetCategoryType.Expense,
        };
    }

    private static DtoTransactionFileType ConvertContextToDto(ContextTransactionFileType src)
    {
        return src switch
        {
            ContextTransactionFileType.Receipt => DtoTransactionFileType.Receipt,
            ContextTransactionFileType.Invoice => DtoTransactionFileType.Invoice,
            ContextTransactionFileType.CreditNote => DtoTransactionFileType.CreditNote,
            ContextTransactionFileType.Quote => DtoTransactionFileType.Quote,
            ContextTransactionFileType.PaymentConfirmation => DtoTransactionFileType.PaymentConfirmation,
            ContextTransactionFileType.Documentation => DtoTransactionFileType.Documentation,
            _ => DtoTransactionFileType.Other,
        };
    }

    private static ContextTransactionFileType ConvertDtoToContext(DtoTransactionFileType src)
    {
        return src switch
        {
            DtoTransactionFileType.Receipt => ContextTransactionFileType.Receipt,
            DtoTransactionFileType.Invoice => ContextTransactionFileType.Invoice,
            DtoTransactionFileType.CreditNote => ContextTransactionFileType.CreditNote,
            DtoTransactionFileType.Quote => ContextTransactionFileType.Quote,
            DtoTransactionFileType.PaymentConfirmation => ContextTransactionFileType.PaymentConfirmation,
            DtoTransactionFileType.Documentation => ContextTransactionFileType.Documentation,
            _ => ContextTransactionFileType.Other,
        };
    }

    private static DtoTaxStatementFileType ConvertContextToDto(ContextTaxStatementFileType src) => src switch
    {
        ContextTaxStatementFileType.TaxReturn => DtoTaxStatementFileType.TaxReturn,
        ContextTaxStatementFileType.TaxAssessment => DtoTaxStatementFileType.TaxAssessment,
        ContextTaxStatementFileType.SupportingDocument => DtoTaxStatementFileType.SupportingDocument,
        _ => DtoTaxStatementFileType.Other,
    };

    private static ContextTaxStatementFileType ConvertDtoToContext(DtoTaxStatementFileType src) => src switch
    {
        DtoTaxStatementFileType.TaxReturn => ContextTaxStatementFileType.TaxReturn,
        DtoTaxStatementFileType.TaxAssessment => ContextTaxStatementFileType.TaxAssessment,
        DtoTaxStatementFileType.SupportingDocument => ContextTaxStatementFileType.SupportingDocument,
        _ => ContextTaxStatementFileType.Other,
    };

    private static DtoTermKind ConvertContextToDto(ContextTermKind src) => src switch
    {
        ContextTermKind.InterestRate => DtoTermKind.InterestRate,
        ContextTermKind.ExpectedReturn => DtoTermKind.ExpectedReturn,
        ContextTermKind.Fee => DtoTermKind.Fee,
        _ => DtoTermKind.Unknown,
    };

    private static ContextTermKind ConvertDtoToContext(DtoTermKind src) => src switch
    {
        DtoTermKind.InterestRate => ContextTermKind.InterestRate,
        DtoTermKind.ExpectedReturn => ContextTermKind.ExpectedReturn,
        DtoTermKind.Fee => ContextTermKind.Fee,
        _ => ContextTermKind.Unknown,
    };

    private static DtoTermValueUnit ConvertContextToDto(ContextTermValueUnit src) => src switch
    {
        ContextTermValueUnit.Amount => DtoTermValueUnit.Amount,
        _ => DtoTermValueUnit.Percentage,
    };

    private static ContextTermValueUnit ConvertDtoToContext(DtoTermValueUnit src) => src switch
    {
        DtoTermValueUnit.Amount => ContextTermValueUnit.Amount,
        _ => ContextTermValueUnit.Percentage,
    };

    // Exhaustive and explicit, never a blanket Adapt: the context and DTO copies of this cadence enum
    // are separate declarations, so a convention-mapped conversion could silently change meaning if
    // the two ever drift.
    //
    // There is deliberately NO arm for the retired ordinal 4 (was Quarterly): after the migration no
    // row holds it, and an arm mapping it would keep a retired value alive on the read path. The
    // `_ =>` fallthrough is unreachable from the write path — TermService refuses an undefined
    // ordinal before the converter is called — so its only remaining job is keeping a STORED bad row
    // readable and repairable rather than 500-ing the account page.
    private static DtoInterval ConvertContextToDto(ContextInterval src) => src switch
    {
        ContextInterval.PerOccurrence => DtoInterval.PerOccurrence,
        ContextInterval.Daily => DtoInterval.Daily,
        ContextInterval.Monthly => DtoInterval.Monthly,
        ContextInterval.Annually => DtoInterval.Annually,
        ContextInterval.PerUnit => DtoInterval.PerUnit,
        ContextInterval.Weekly => DtoInterval.Weekly,
        _ => DtoInterval.OneTime,
    };

    private static ContextInterval ConvertDtoToContext(DtoInterval src) => src switch
    {
        DtoInterval.PerOccurrence => ContextInterval.PerOccurrence,
        DtoInterval.Daily => ContextInterval.Daily,
        DtoInterval.Monthly => ContextInterval.Monthly,
        DtoInterval.Annually => ContextInterval.Annually,
        DtoInterval.PerUnit => ContextInterval.PerUnit,
        DtoInterval.Weekly => ContextInterval.Weekly,
        _ => ContextInterval.OneTime,
    };
}
