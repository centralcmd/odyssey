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
using ContextPropertyFileType = Odyssey.Context.PropertyFileType;
using DtoPropertyFileType = Odyssey.Dtos.Finance.PropertyFileType;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextInterval = Odyssey.Context.Interval;
using DtoTermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using ContextBudgetItem = Odyssey.Context.BudgetItem;
using DtoExistingBudgetItem = Odyssey.Dtos.Finance.ExistingBudgetItem;
using ContextTransaction = Odyssey.Context.Transaction;
using ContextTransactionTag = Odyssey.Context.TransactionTag;
using DtoExistingTransaction = Odyssey.Dtos.Finance.ExistingTransaction;
using DtoExistingTransactionTag = Odyssey.Dtos.Finance.ExistingTransactionTag;
using TransactionTagIcons = Odyssey.Dtos.Finance.TransactionTagIcons;

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

            // Every Context↔Dtos enum pair maps by ordinal through EnumMirror; EnumMirrorParityTests pins
            // each pair to identical names and ordinals, which is what makes that sound (issue #287 M6).
            // The fallback is where an undefined stored value (a retired ordinal, a hand edit) lands.
            Mirror(DtoAccountType.Unknown, ContextAccountType.Unknown);
            Mirror(DtoAccountFileType.Other, ContextAccountFileType.Other);
            Mirror(DtoBudgetCategoryType.Expense, ContextBudgetCategoryType.Expense);
            Mirror(DtoTransactionFileType.Other, ContextTransactionFileType.Other);
            Mirror(DtoTaxStatementFileType.Other, ContextTaxStatementFileType.Other);
            Mirror(DtoPropertyFileType.Other, ContextPropertyFileType.Other);

            // No fallback, and THROWING on anything else (issue #192 §8). A fallback arm here once read
            // `_ => Percentage`, so an unmapped member would have been persisted — and shown — as a
            // percentage. There is no safe member to fall back to, and an unknown ordinal cannot be
            // stored (CK_Terms_ValueMatchesUnit), so reaching the throw means the schema was bypassed.
            TypeAdapterConfig<ContextTermValueUnit, DtoTermValueUnit>
                .NewConfig()
                .MapWith(src => EnumMirror.ConvertOrThrow<ContextTermValueUnit, DtoTermValueUnit>(src));

            TypeAdapterConfig<DtoTermValueUnit, ContextTermValueUnit>
                .NewConfig()
                .MapWith(src => EnumMirror.ConvertOrThrow<DtoTermValueUnit, ContextTermValueUnit>(src));

            // The retired ordinal 4 (was Quarterly) is defined in neither copy, so a stored row still
            // holding it reads as OneTime: readable and repairable rather than 500-ing the account page.
            // The write path never reaches that fallback — TermService refuses an undefined ordinal first.
            //
            // HEADS-UP (Mapster version): this MapWith converter is registered for the NON-nullable
            // Interval pair, but Term.Interval is nullable and maps to the (different) nullable
            // Dtos.Interval. Mapster 10.0.8 lifts this converter over Nullable<T> with a
            // null guard (null -> null); Mapster 10.0.9 regressed that lifting and calls src.Value
            // unconditionally, throwing "Nullable object must have a value" for null intervals
            // (interest-rate/expected-return terms legitimately have none). That broke 17 term
            // tests, so Mapster is pinned to 10.0.8 in Directory.Packages.props. Before accepting a bump
            // to >= 10.0.9, register null-guarded nullable converters here, e.g.
            //   TypeAdapterConfig<ContextInterval?, DtoInterval?>.NewConfig()
            //       .MapWith(src => src.HasValue ? EnumMirror.Convert(src.Value, DtoInterval.OneTime) : null);
            // (and the reverse), then re-verify the term suites stay green.
            Mirror(DtoInterval.OneTime, ContextInterval.OneTime);

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

            // Issue #279. Every ExistingTransactionTag on every read path — the tag list, budget items,
            // reports, the account/contract/property smart-tag lists and the tags embedded on a
            // transaction — is projected through this one registration, so an unknown stored key (a
            // hand edit, a restore, a key retired from the catalogue) reaches no client as anything but
            // null. Explicit rather than convention-mapped so the raw column can never leak through.
            TypeAdapterConfig<ContextTransactionTag, DtoExistingTransactionTag>
                .NewConfig()
                .Map(dest => dest.Icon, src => TransactionTagIcons.Normalize(src.Icon));

            // Issue #279. The tag order and the display icon are computed after materialisation from the
            // already-projected tags, so every producer of ExistingTransaction (the transaction list and
            // detail, an account's transactions, the budget report, the contract smart-tag list) gets
            // both, the EF InMemory tier runs the same code, and OrdinalIgnoreCase needs no translation.
            // TransactionTagIcons is the only implementation of either rule; a source-lint pins that.
            TypeAdapterConfig<ContextTransaction, DtoExistingTransaction>
                .NewConfig()
                .AfterMapping((_, dest) =>
                {
                    dest.TransactionTags = TransactionTagIcons.Order(dest.TransactionTags);
                    dest.DisplayIcon = TransactionTagIcons.Resolve(dest.TransactionTags);
                });

            // (The former Account→ExistingAccount Ignore(Custodian) pin was removed with the Contact
            // move: Account no longer has a Custodian navigation — only the scalar CustodianId — so there
            // is nothing for Mapster to auto-map onto the slim Custodian DTO. The service resolves
            // ExistingAccount.Custodian explicitly via IContactLookup. Contact read-projection Mapster
            // config moved to Odyssey.Core.Journal/ContactMapsterConfig.cs with the aggregate (issue #325).)

            configured = true;
        }
    }

    private static void Mirror<TDto, TContext>(TDto dtoFallback, TContext contextFallback)
        where TDto : struct, Enum
        where TContext : struct, Enum
    {
        TypeAdapterConfig<TContext, TDto>.NewConfig().MapWith(src => EnumMirror.Convert(src, dtoFallback));
        TypeAdapterConfig<TDto, TContext>.NewConfig().MapWith(src => EnumMirror.Convert(src, contextFallback));
    }
}
