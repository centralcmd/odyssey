/* templates/kit-app.js — shared loader for the Odyssey Web kit templates.
   ----------------------------------------------------------------------------
   Each template under templates/<slug>/ is a Design Component that mounts ONE
   of the kit's existing React pages (Dashboard, Accounts, Login, …). Those
   pages are authored as JSX across many files that register their exports on
   `window`. This loader pulls the whole kit in once, in the right order, so any
   template can mount its page by name.

   Why a loader (and not <script> tags in each template's helmet):
     • React/ReactDOM are provided by the DC runtime — we must NOT load a second
       copy (it crashes the runtime), so we wait for the runtime's React.
     • The DC runtime does not auto-transform <script type="text/babel">, so we
       transpile the kit's JSX ourselves with @babel/standalone.
     • Paths are resolved relative to THIS file's location, so the loader works
       at any folder depth.

   Contract:
     window.__KIT_PROPS  — a props bag (with no-op handlers) safe to spread into
                           any kit page component.
     window.__kitReady   — set true once every dependency has been evaluated.
   A template's logic class polls for `window.__kitReady && window[ComponentName]`
   then mounts `React.createElement(window[ComponentName], window.__KIT_PROPS)`. */

(function () {
  // This file lives at <root>/templates/kit-app.js — derive <root> from its URL
  // so fetches resolve regardless of how deep the importing template sits.
  const SELF = (document.currentScript && document.currentScript.src) ||
               new URL('kit-app.js', location.href).href;
  const ROOT = new URL('../', new URL('.', SELF)).href; // project root URL

  // Props safe to spread into ANY kit page (extra keys are ignored by pages
  // that don't read them). Navigation/auth handlers are inert in a template.
  window.__KIT_PROPS = {
    onNavigate: function () {},
    onLogout: function () {},
    onLogin: function () {},
    onGoRegister: function () {},
    onGoLogin: function () {},
    onGoForgot: function () {},
    onGoReset: function () {},
    onDone: function () {},
    onToggleDark: function () {},
    darkMode: true,
    // Pages that read tweak values (Contracts' ending-soon window) get the
    // kit's own defaults; pages that don't read them ignore the key.
    tweaks: { endingWindowDays: 45 },
  };

  // ---- Stylesheets (mirror ui_kits/web/index.html) ----
  ['colors_and_type.css',
   'ui_kits/web/kit.css',
   'ui_kits/web/account-signals.css',
   'ui_kits/web/admin.css',
   'ui_kits/web/account.css',
   'ui_kits/web/onboarding.css',
   'ui_kits/web/account-terms.css',
   'ui_kits/web/account-estimates.css',
   'ui_kits/web/tax-statements.css',
   'ui_kits/web/insurance.css',
   'ui_kits/web/legal.css',
   'ui_kits/web/contracts.css',
   'ui_kits/web/subscriptions.css',
   'ui_kits/web/journal.css',
   'ui_kits/web/photos.css',
   'ui_kits/web/calendar.css',
   'ui_kits/web/contacts.css'].forEach(function (href) {
    const l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = ROOT + href;
    document.head.appendChild(l);
  });

  const until = function (cond) {
    return new Promise(function (res) {
      (function tick() { cond() ? res() : setTimeout(tick, 30); })();
    });
  };

  const ensureBabel = function () {
    if (window.Babel) return Promise.resolve();
    return new Promise(function (res, rej) {
      const s = document.createElement('script');
      s.src = 'https://unpkg.com/@babel/standalone@7.29.0/babel.min.js';
      s.integrity = 'sha384-m08KidiNqLdpJqLq95G/LEi8Qvjl/xUYll3QILypMoQ65QorJ9Lvtp2RXYGBFj1y';
      s.crossOrigin = 'anonymous';
      s.onload = res;
      s.onerror = rej;
      document.head.appendChild(s);
    });
  };

  const run = function (code, jsx, rel) {
    try {
      const out = jsx ? window.Babel.transform(code, { presets: ['react'] }).code : code;
      (0, eval)(out);
    } catch (e) { console.error('[kit-app] failed to evaluate ' + rel, e); }
  };
  const fetchText = function (rel) {
    return fetch(ROOT + rel)
      .then(function (r) { return r.text(); })
      .catch(function (e) { console.error('[kit-app] failed to fetch ' + rel, e); return ''; });
  };

  // Plain JS first (compiled bundle + seed data), then the kit's JSX in the
  // same order ui_kits/web/index.html loads it. Eval order matters (shared
  // window globals), so we FETCH every file in parallel but EVAL in sequence.
  const PLAIN = ['_ds_bundle.js', 'ui_kits/web/data.js', 'ui_kits/web/tax-data.js', 'ui_kits/web/insurance-data.js', 'ui_kits/web/contracts-data.js', 'ui_kits/web/subscriptions-data.js', 'ui_kits/web/journal-data.js', 'ui_kits/web/photos-data.js', 'ui_kits/web/calendar-data.js', 'ui_kits/web/system-settings-data.js', 'ui_kits/web/legal-data.js'];
  const JSX = [
    'Components.jsx', 'profile-fields.jsx', 'page-header.jsx', 'AppShell.jsx', 'Login.jsx', 'ForgotPassword.jsx', 'ResetPassword.jsx', 'ChangePasswordRequired.jsx', 'Onboarding.jsx', 'Dashboard.jsx',
    'AddAccountModal.jsx', 'AddFileModal.jsx', 'FileViewerModal.jsx', 'AnalyzeFileModal.jsx',
    'AddTransactionModal.jsx', 'AddTermModal.jsx', 'AccountTerms.jsx',
    'AddEstimateModal.jsx', 'AccountEstimates.jsx', 'Accounts.jsx',
    'Files.jsx', 'Transactions.jsx', 'TransactionTags.jsx', 'ContactImportModal.jsx', 'Contacts.jsx',
    'Currencies.jsx', 'ExchangeRates.jsx', 'AddBudgetModal.jsx', 'AddBudgetItemModal.jsx',
    'Budgets.jsx', 'AddTaxStatementModal.jsx', 'TaxStatements.jsx',
    'AddInsurancePolicyModal.jsx', 'AddRenewalModal.jsx', 'InsuranceUploadModal.jsx', 'AddPolicyPartyModal.jsx', 'Insurance.jsx',
    'AddContractModal.jsx', 'AddContractPartyModal.jsx', 'AddContractFileModal.jsx', 'Contracts.jsx',
    'AddSubscriptionModal.jsx', 'Subscriptions.jsx',
    'ImportJournalEntriesModal.jsx', 'Journal.jsx', 'ImportTasksModal.jsx', 'Tasks.jsx',
    'AddCalendarEventModal.jsx', 'ExportCalendarEventsModal.jsx', 'ManageCalendarsModal.jsx', 'ImportCalendarModal.jsx', 'Calendar.jsx',
    'Users.jsx', 'Roles.jsx', 'FileAnalysisLog.jsx', 'SystemSettings.jsx', 'Preferences.jsx', 'AccountTwoFactor.jsx',
    'Account.jsx', 'ConfirmEmail.jsx', 'Photos.jsx',
    'AcceptTerms.jsx', 'LegalDocuments.jsx',
  ].map(function (f) { return 'ui_kits/web/' + f; });

  (async function () {
    await until(function () { return window.React && window.ReactDOM && window.ReactDOM.createRoot; });
    await ensureBabel();
    await until(function () { return !!window.Babel; });

    // Kick off every fetch up front (parallel), then eval in dependency order.
    const order = PLAIN.concat(JSX);
    const texts = await Promise.all(order.map(fetchText));
    order.forEach(function (rel, i) {
      run(texts[i], JSX.indexOf(rel) !== -1, rel);
    });
    window.__kitReady = true;
  })();
})();
