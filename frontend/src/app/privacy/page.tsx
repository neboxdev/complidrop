import type { Metadata } from "next";
import Link from "next/link";
import {
  ArticleShell,
  Breadcrumbs,
  ContentH1,
  Lead,
  LegalSection,
} from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { LEGAL_ADDRESS, LEGAL_ENTITY, SITE_NAME, SUPPORT_EMAIL } from "@/lib/site";

export const metadata: Metadata = pageMetadata({
  title: "Privacy Policy",
  description:
    "How CompliDrop collects, uses, stores, and protects your account information and the compliance documents you upload.",
  path: "/privacy",
});

// Fixed effective date — a legal document's "last updated" must not change on
// every render/build (so never `new Date()` here). Bump by hand on real edits.
const EFFECTIVE_DATE = "August 7, 2026";

export default function PrivacyPolicyPage() {
  return (
    <ArticleShell>
      <JsonLd
        data={breadcrumbLd([
          { name: "Home", path: "/" },
          { name: "Privacy Policy", path: "/privacy" },
        ])}
      />
      <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "Privacy Policy" }]} />

      <ContentH1>Privacy Policy</ContentH1>
      <Lead>
        {SITE_NAME}, operated by {LEGAL_ENTITY}, helps small businesses track
        certificates of insurance, licenses, and permits. This policy explains what we
        collect, why, who we share it with, and the choices you have. We keep it in
        plain English on purpose.
      </Lead>
      <p className="mt-4 text-sm text-muted-foreground">Last updated {EFFECTIVE_DATE}.</p>

      <LegalSection title="Information we collect">
        <p>
          <strong className="text-foreground">Account information.</strong> When you
          create an account we collect your name, email address, business name,
          and optional details you provide such as industry, company size, and time
          zone.
        </p>
        <p>
          <strong className="text-foreground">Documents you upload.</strong> The
          certificates of insurance, licenses, and permits you (or your vendors)
          upload, along with the fields we read from them — names, dates, coverage
          types, and limits.
        </p>
        <p>
          <strong className="text-foreground">Usage and security records.</strong> We
          keep an audit log of key actions in your account (uploads, edits, sign-ins)
          and basic technical data such as IP address and browser type, which we use
          to operate, secure, and troubleshoot the service.
        </p>
        <p>
          <strong className="text-foreground">Payment information.</strong> Paid plans
          are billed through Stripe. Your card details are entered directly with
          Stripe and processed by them — {SITE_NAME} never sees or stores your full
          card number.
        </p>
      </LegalSection>

      <LegalSection title="How we use your information">
        <p>We use the information above to:</p>
        <ul className="ml-5 list-disc space-y-1.5">
          <li>provide the service — read your documents, check them against your requirements, and send expiration reminders;</li>
          <li>create and secure your account and isolate your data from other customers;</li>
          <li>process payments and manage your subscription;</li>
          <li>respond to support requests and send service-related notices;</li>
          <li>detect, prevent, and investigate abuse, and meet legal obligations.</li>
        </ul>
        <p>
          We do <strong className="text-foreground">not</strong> sell your data, and we
          do not use the documents you upload to train public AI models.
        </p>
      </LegalSection>

      <LegalSection title="Service providers we share data with">
        <p>
          We share data with the vendors that help us run {SITE_NAME}, and only to
          the extent each needs it to do its job. These include:
        </p>
        <ul className="ml-5 list-disc space-y-1.5">
          <li><strong className="text-foreground">Stripe</strong> — payment processing.</li>
          <li><strong className="text-foreground">Google Cloud (Document AI &amp; Vertex AI)</strong> — reading text and fields from your uploaded documents.</li>
          <li><strong className="text-foreground">Microsoft Azure</strong> — encrypted storage of your uploaded files.</li>
          <li><strong className="text-foreground">Neon</strong> — our application database.</li>
          <li><strong className="text-foreground">Railway</strong> — hosting the application servers that receive and process your uploads.</li>
          <li><strong className="text-foreground">Vercel</strong> — hosting and delivering the {SITE_NAME} web app you use in your browser.</li>
          <li><strong className="text-foreground">Resend</strong> — sending reminder and notification emails.</li>
          <li><strong className="text-foreground">PostHog</strong> — product analytics that help us understand and improve how the app is used.</li>
          <li><strong className="text-foreground">Sentry</strong> — error monitoring so we can fix problems quickly.</li>
        </ul>
        <p>
          We process and store data primarily in the United States. We work only with
          providers that commit to protecting it, and we share only what each provider
          needs to perform its function.
        </p>
        <p>
          We may also disclose information if required by law, or to protect the
          rights, safety, and security of {SITE_NAME}, our customers, or the public.
        </p>
      </LegalSection>

      <LegalSection title="Cookies and analytics">
        <p>
          We use essential, first-party cookies to keep you signed in securely. We
          also use a product-analytics tool (PostHog) that sets a cookie and records
          how the app is used — such as pages viewed and features used — so we can
          understand and improve it. We do not use cookies for advertising, we do not
          run third-party ad trackers, and we do not sell your data.
        </p>
      </LegalSection>

      <LegalSection title="How we protect your data">
        <p>
          Data is transmitted over encrypted connections and stored privately. Each
          account&apos;s data is logically isolated from every other account,
          passwords are stored only as salted hashes, and changes are written to an
          audit log. No system is perfectly secure, but we work to protect your
          information using industry-standard measures.
        </p>
      </LegalSection>

      {/* #398 / ADR 0013 Amendment 1 / counsel gate CLM-7. The retired sentence promised that
          "we delete or de-identify your data within a reasonable period" after closure. No purge
          job exists anywhere in the codebase: `AuthEndpoints.DeleteAccount` scrubs the account
          holder's email + name and stamps `DeletedAt` on the user and the org, and that is all.
          Every clause below is traceable — the scrub and the org tombstone to that endpoint, the
          Stripe cancel to its `CancelSubscriptionAsync` call (which aborts the whole request if
          it fails), the reminder stop to `ReminderBackgroundService`'s `o.DeletedAt == null`
          filter, and the retained set to ADR 0013 § Consequences. NO retention period is stated,
          deliberately: a number no job enforces would recreate the same defect in a new sentence. */}
      <LegalSection title="How long we keep it">
        <p>
          We keep your account information and documents for as long as your account
          is active.
        </p>
        <p>
          When you close your account from Settings, we clear your name and email from
          your account record straight away, cancel any paid plan, and stop sending
          reminders. Closing makes the account unusable — but on its own it is not a
          deletion of everything the account held. Your organization&apos;s records stay
          with us: the vendors you added and their contact details, your documents and
          the files uploaded for them, reminder history, billing records, and our log of
          what happened in the account.
        </p>
        <p>
          We have not set a fixed disposal period for those records, so they stay with us
          until you ask us to delete them. To ask, use the contact details at the end of
          this page. We&apos;ll check the request is really yours, delete what we can, and
          keep only what we must to meet legal, tax, or security obligations.
        </p>
      </LegalSection>

      {/* The portal's notice-at-collection links here, and a vendor arriving from it is not the
          reader the rest of this policy addresses — they have no account and never will (#404 /
          counsel gate CLM-5). The "information inside a document" paragraph below covers the person
          NAMED on a certificate; this covers the person who UPLOADS one. */}
      <LegalSection title="If you were sent an upload link">
        <p>
          Some people reach {SITE_NAME} without ever creating an account: one of our
          customers sends a vendor, contractor, or supplier a link to a secure upload
          page and asks for a current certificate, license, or permit. If that is you,
          this section is the short version.
        </p>
        <p>
          You do not need an account, and we do not ask you to create one. When you use
          that page we collect the file you upload, along with the basic technical data
          described above such as IP address and browser type. Unlike the rest of the
          site, that page sets no cookies and we do not measure how it is used, because
          the link you were sent is itself the key to it. Your file is stored, and it is read
          automatically by the service providers listed above — that automated reading
          is how the dates, names, and coverage amounts on it are turned into fields —
          so the business that sent you the link can check it against what they require.
        </p>
        {/* #398: read this together with "How long we keep it" above. This paragraph used to end
            the reader's retention question at the business ("how long it keeps its copy"), which
            left an account-less vendor with no answer about OUR copy — and ours outlives the
            customer's account, since closing it purges nothing. */}
        <p>
          That business decides what to do with the document it asked you for, so a
          request about it is usually fastest with them. We keep our own copy while they
          use {SITE_NAME}, and afterwards as described in &quot;How long we keep it&quot;
          above. You can also reach us using the contact details at the end of this page
          and we&apos;ll help route your request.
        </p>
      </LegalSection>

      <LegalSection title="Your choices and rights">
        <p>
          You can access and update most of your information from within the app. You
          can also ask us to provide a copy of your data, correct it, or delete it.
          Depending on where you live, you may have additional rights — to know what
          we collect, to access or correct it, to delete it, and to opt out of any
          &quot;sale&quot; or &quot;sharing&quot; of personal information (we don&apos;t
          sell personal information, and we don&apos;t share it for targeted
          advertising). We honor these rights as required by applicable law and
          aim to respond within 30 days.
        </p>
        <p>
          If your information appears inside a document that one of our customers
          uploaded — for example, a certificate naming you — that business controls
          that record. Contact them directly, or email us and we&apos;ll help route
          your request.
        </p>
      </LegalSection>

      <LegalSection title="Children">
        <p>
          {SITE_NAME} is a business tool, not intended for individual consumers or
          minors, and we do not knowingly collect personal information from children.
        </p>
      </LegalSection>

      <LegalSection title="Changes to this policy">
        <p>
          We may update this policy as the service evolves. When we make material
          changes we will update the date above and, where appropriate, notify you in
          the app or by email.
        </p>
      </LegalSection>

      <LegalSection title="Contact us">
        <p>
          Questions about this policy or your data? Email us at{" "}
          <a href={`mailto:${SUPPORT_EMAIL}`} className="text-primary hover:underline">
            {SUPPORT_EMAIL}
          </a>{" "}
          or visit our{" "}
          <Link href="/contact" className="text-primary hover:underline">
            contact page
          </Link>
          .
        </p>
        <p>
          {SITE_NAME} is operated by {LEGAL_ENTITY}, {LEGAL_ADDRESS}.
        </p>
      </LegalSection>
    </ArticleShell>
  );
}
