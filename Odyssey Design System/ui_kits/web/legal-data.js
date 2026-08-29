/* =============================================================
   legal-data.js — sample License + Terms-of-Service content for the
   License / ToS acceptance feature (spec §5–§7). Plain <script> seed data,
   loaded before the JSX kit pages, exposed on window.OdysseyLegal.

     licenseText  — the repository LICENSE file's content (of record).
     licenseSha   — SHA-256 hex digest of that text, computed server-side.
     tosVersions  — every published TermsOfServiceVersion, newest first.
     currentTosId — id of the current (highest PublishedAt) version.

   None of this is authoritative in the mock — the live app reads
   GET /api/legal/license and /terms-of-service/current. It exists so the
   interstitial and admin panel render against realistic content.
   ============================================================= */
(function () {
  const licenseText = `BSD 2-Clause License

Copyright (c) 2026, Odyssey

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.`;

  const tosBody = (rev) => `Odyssey Terms of Service
Effective ${rev.effective}

1. Your account
You are responsible for the security of your Odyssey account and for all
activity that occurs under it. Keep your credentials private and enable
two-factor authentication where your administrator requires it. Notify an
administrator promptly if you believe your account has been compromised.

2. Acceptable use
Odyssey stores personal financial records. Use it only for lawful purposes and
only for records you are entitled to keep. Do not attempt to access another
person's data, disrupt the service, or circumvent the access controls and
permission claims that govern the workspace.

3. Your data
The records you enter remain yours. Odyssey processes them to provide the
service to you under the agreement between you and the operator of this
deployment. You may export your data at any time from System Settings, subject
to the limits your administrator configures.

4. Availability and changes
This deployment is operated by your organization, not by the Odyssey project.
Availability, backups, and retention are set by your administrator. These terms
may be updated; when they are, you will be asked to review and accept the new
version before continuing to use the service.

5. Termination
Your administrator may disable or delete your account. Where required by law,
records evidencing your acceptance of these terms are retained after deletion
in a form that identifies you only if you later dispute that acceptance.

${rev.note}`;

  const tosVersions = [
    {
      id: 3, publishedAt: '2026-08-06T14:22:00Z', effective: '6 August 2026',
      publishedByUserId: 'u_017', publishedByDisplayName: 'Priya Anand',
      note: '6. Contact\nQuestions about these terms should be directed to your workspace administrator.',
    },
    {
      id: 2, publishedAt: '2026-05-19T09:10:00Z', effective: '19 May 2026',
      publishedByUserId: 'u_004', publishedByDisplayName: 'Marcus Reyes',
      note: '6. Contact\nQuestions about these terms should be directed to your administrator.',
    },
    {
      id: 1, publishedAt: '2026-02-02T16:47:00Z', effective: '2 February 2026',
      publishedByUserId: null, publishedByDisplayName: null,
      note: '6. Contact\nContact your administrator with any questions.',
    },
  ].map((v) => ({ ...v, content: tosBody(v) }));

  window.OdysseyLegal = {
    licenseText,
    licenseSha: '3b8f1c9a5e2d4f7061a8c3be9d02f4157a6c8e1b0d9f2a34c5e67089ab1cd234',
    tosVersions,
    currentTosId: 3,
    currentTos: tosVersions[0],
  };
})();
