using Odyssey.Dtos;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mapster;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextBudgetCategoryType = Odyssey.Context.BudgetCategoryType;
using ContextTransactionFileType = Odyssey.Context.TransactionFileType;
using ContextTaxStatementFileType = Odyssey.Context.TaxStatementFileType;
using ContextInsurancePolicyType = Odyssey.Context.InsurancePolicyType;
using ContextPolicyFileType = Odyssey.Context.PolicyFileType;
using ContextBillingInterval = Odyssey.Context.BillingInterval;
using DtoBillingInterval = Odyssey.Dtos.Finance.BillingInterval;
using DtoInsurancePolicyType = Odyssey.Dtos.Finance.InsurancePolicyType;
using DtoPolicyFileType = Odyssey.Dtos.Finance.PolicyFileType;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoBudgetCategoryType = Odyssey.Dtos.Finance.BudgetCategoryType;
using DtoTransactionFileType = Odyssey.Dtos.Finance.TransactionFileType;
using DtoTaxStatementFileType = Odyssey.Dtos.Finance.TaxStatementFileType;
using ContextTermKind = Odyssey.Context.TermKind;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextBillingPeriod = Odyssey.Context.BillingPeriod;
using DtoTermKind = Odyssey.Dtos.Finance.TermKind;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using DtoBillingPeriod = Odyssey.Dtos.Finance.BillingPeriod;

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

            TypeAdapterConfig<ContextInsurancePolicyType, DtoInsurancePolicyType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoInsurancePolicyType, ContextInsurancePolicyType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextPolicyFileType, DtoPolicyFileType>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoPolicyFileType, ContextPolicyFileType>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

            TypeAdapterConfig<ContextBillingInterval, DtoBillingInterval>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoBillingInterval, ContextBillingInterval>
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
            // BillingPeriod pair, but AccountTerm.BillingPeriod is nullable and maps to the (different)
            // nullable Dtos.BillingPeriod. Mapster 10.0.8 lifts this converter over Nullable<T> with a
            // null guard (null -> null); Mapster 10.0.9 regressed that lifting and calls src.Value
            // unconditionally, throwing "Nullable object must have a value" for null billing periods
            // (interest-rate/expected-return terms legitimately have none). That broke 17 AccountTerm
            // tests, so Mapster is pinned to 10.0.8 in Directory.Packages.props. Before accepting a bump
            // to >= 10.0.9, register null-guarded nullable converters here, e.g.
            //   TypeAdapterConfig<ContextBillingPeriod?, DtoBillingPeriod?>.NewConfig()
            //       .MapWith(src => src.HasValue ? ConvertContextToDto(src.Value) : null);
            // (and the reverse), then re-verify the AccountTerm suites stay green.
            TypeAdapterConfig<ContextBillingPeriod, DtoBillingPeriod>
                .NewConfig()
                .MapWith(src => ConvertContextToDto(src));

            TypeAdapterConfig<DtoBillingPeriod, ContextBillingPeriod>
                .NewConfig()
                .MapWith(src => ConvertDtoToContext(src));

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

    private static DtoInsurancePolicyType ConvertContextToDto(ContextInsurancePolicyType src) => src switch
    {
        ContextInsurancePolicyType.Home => DtoInsurancePolicyType.Home,
        ContextInsurancePolicyType.Contents => DtoInsurancePolicyType.Contents,
        ContextInsurancePolicyType.Building => DtoInsurancePolicyType.Building,
        ContextInsurancePolicyType.Vehicle => DtoInsurancePolicyType.Vehicle,
        ContextInsurancePolicyType.Travel => DtoInsurancePolicyType.Travel,
        ContextInsurancePolicyType.Life => DtoInsurancePolicyType.Life,
        ContextInsurancePolicyType.Health => DtoInsurancePolicyType.Health,
        ContextInsurancePolicyType.Accident => DtoInsurancePolicyType.Accident,
        ContextInsurancePolicyType.Liability => DtoInsurancePolicyType.Liability,
        ContextInsurancePolicyType.Pet => DtoInsurancePolicyType.Pet,
        ContextInsurancePolicyType.Property => DtoInsurancePolicyType.Property,
        _ => DtoInsurancePolicyType.Other,
    };

    private static ContextInsurancePolicyType ConvertDtoToContext(DtoInsurancePolicyType src) => src switch
    {
        DtoInsurancePolicyType.Home => ContextInsurancePolicyType.Home,
        DtoInsurancePolicyType.Contents => ContextInsurancePolicyType.Contents,
        DtoInsurancePolicyType.Building => ContextInsurancePolicyType.Building,
        DtoInsurancePolicyType.Vehicle => ContextInsurancePolicyType.Vehicle,
        DtoInsurancePolicyType.Travel => ContextInsurancePolicyType.Travel,
        DtoInsurancePolicyType.Life => ContextInsurancePolicyType.Life,
        DtoInsurancePolicyType.Health => ContextInsurancePolicyType.Health,
        DtoInsurancePolicyType.Accident => ContextInsurancePolicyType.Accident,
        DtoInsurancePolicyType.Liability => ContextInsurancePolicyType.Liability,
        DtoInsurancePolicyType.Pet => ContextInsurancePolicyType.Pet,
        DtoInsurancePolicyType.Property => ContextInsurancePolicyType.Property,
        _ => ContextInsurancePolicyType.Other,
    };

    private static DtoPolicyFileType ConvertContextToDto(ContextPolicyFileType src) => src switch
    {
        ContextPolicyFileType.Contract => DtoPolicyFileType.Contract,
        ContextPolicyFileType.Invoice => DtoPolicyFileType.Invoice,
        ContextPolicyFileType.TermsAndConditions => DtoPolicyFileType.TermsAndConditions,
        ContextPolicyFileType.PolicyDocument => DtoPolicyFileType.PolicyDocument,
        ContextPolicyFileType.ClaimDocument => DtoPolicyFileType.ClaimDocument,
        _ => DtoPolicyFileType.Other,
    };

    private static ContextPolicyFileType ConvertDtoToContext(DtoPolicyFileType src) => src switch
    {
        DtoPolicyFileType.Contract => ContextPolicyFileType.Contract,
        DtoPolicyFileType.Invoice => ContextPolicyFileType.Invoice,
        DtoPolicyFileType.TermsAndConditions => ContextPolicyFileType.TermsAndConditions,
        DtoPolicyFileType.PolicyDocument => ContextPolicyFileType.PolicyDocument,
        DtoPolicyFileType.ClaimDocument => ContextPolicyFileType.ClaimDocument,
        _ => ContextPolicyFileType.Other,
    };

    private static DtoBillingInterval ConvertContextToDto(ContextBillingInterval src) => src switch
    {
        ContextBillingInterval.Daily => DtoBillingInterval.Daily,
        ContextBillingInterval.Weekly => DtoBillingInterval.Weekly,
        ContextBillingInterval.Yearly => DtoBillingInterval.Yearly,
        _ => DtoBillingInterval.Monthly,
    };

    private static ContextBillingInterval ConvertDtoToContext(DtoBillingInterval src) => src switch
    {
        DtoBillingInterval.Daily => ContextBillingInterval.Daily,
        DtoBillingInterval.Weekly => ContextBillingInterval.Weekly,
        DtoBillingInterval.Yearly => ContextBillingInterval.Yearly,
        _ => ContextBillingInterval.Monthly,
    };

    private static DtoTermKind ConvertContextToDto(ContextTermKind src) => src switch
    {
        ContextTermKind.InterestRate => DtoTermKind.InterestRate,
        ContextTermKind.ExpectedReturn => DtoTermKind.ExpectedReturn,
        ContextTermKind.ManagementFee => DtoTermKind.ManagementFee,
        ContextTermKind.ServiceFee => DtoTermKind.ServiceFee,
        ContextTermKind.TransactionFee => DtoTermKind.TransactionFee,
        ContextTermKind.OtherFee => DtoTermKind.OtherFee,
        _ => DtoTermKind.Unknown,
    };

    private static ContextTermKind ConvertDtoToContext(DtoTermKind src) => src switch
    {
        DtoTermKind.InterestRate => ContextTermKind.InterestRate,
        DtoTermKind.ExpectedReturn => ContextTermKind.ExpectedReturn,
        DtoTermKind.ManagementFee => ContextTermKind.ManagementFee,
        DtoTermKind.ServiceFee => ContextTermKind.ServiceFee,
        DtoTermKind.TransactionFee => ContextTermKind.TransactionFee,
        DtoTermKind.OtherFee => ContextTermKind.OtherFee,
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

    private static DtoBillingPeriod ConvertContextToDto(ContextBillingPeriod src) => src switch
    {
        ContextBillingPeriod.PerTransaction => DtoBillingPeriod.PerTransaction,
        ContextBillingPeriod.Daily => DtoBillingPeriod.Daily,
        ContextBillingPeriod.Monthly => DtoBillingPeriod.Monthly,
        ContextBillingPeriod.Quarterly => DtoBillingPeriod.Quarterly,
        ContextBillingPeriod.Annually => DtoBillingPeriod.Annually,
        _ => DtoBillingPeriod.OneTime,
    };

    private static ContextBillingPeriod ConvertDtoToContext(DtoBillingPeriod src) => src switch
    {
        DtoBillingPeriod.PerTransaction => ContextBillingPeriod.PerTransaction,
        DtoBillingPeriod.Daily => ContextBillingPeriod.Daily,
        DtoBillingPeriod.Monthly => ContextBillingPeriod.Monthly,
        DtoBillingPeriod.Quarterly => ContextBillingPeriod.Quarterly,
        DtoBillingPeriod.Annually => ContextBillingPeriod.Annually,
        _ => ContextBillingPeriod.OneTime,
    };
}
