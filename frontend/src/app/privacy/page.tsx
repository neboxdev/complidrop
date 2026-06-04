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
import { SITE_NAME, SUPPORT_EMAIL } from "@/lib/site";

export const metadata: Metadata = pageMetadata({
  title: "Privacy Policy",
  description:
    "How CompliDrop collects, uses, stores, and protects your account information and the compliance documents you upload.",
  path: "/privacy",
});

// Fixed effective date — a legal document's "last updated" must not change on
// every render/build (so never `new Date()` here). Bump by hand on real edits.
const EFFECTIVE_DATE = "June 4, 2026";

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
        {SITE_NAME} helps small businesses track certificates of insurance,
        licenses, and permits. This policy explains what we collect, why, who we
        share it with, and the choices you have. We keep it in plain English on
        purpose.
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
          We share data only with the vendors that help us run {SITE_NAME}, and only
          to the extent each needs it to do its job. These include:
        </p>
        <ul className="ml-5 list-disc space-y-1.5">
          <li><strong className="text-foreground">Stripe</strong> — payment processing.</li>
          <li><strong className="text-foreground">Google Cloud (Document AI &amp; Vertex AI)</strong> — reading text and fields from your uploaded documents.</li>
          <li><strong className="text-foreground">Microsoft Azure</strong> — encrypted storage of your uploaded files.</li>
          <li><strong className="text-foreground">Neon</strong> — our application database.</li>
          <li><strong className="text-foreground">Resend</strong> — sending reminder and notification emails.</li>
          <li><strong className="text-foreground">Sentry</strong> — error monitoring so we can fix problems quickly.</li>
        </ul>
        <p>
          We may also disclose information if required by law, or to protect the
          rights, safety, and security of {SITE_NAME}, our customers, or the public.
        </p>
      </LegalSection>

      <LegalSection title="Cookies">
        <p>
          We use a small number of essential, first-party cookies to keep you signed
          in securely. They are not used for advertising or cross-site tracking, and
          we do not run third-party ad trackers.
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

      <LegalSection title="How long we keep it">
        <p>
          We keep your account information and documents for as long as your account
          is active. If you close your account, we delete or de-identify your data
          within a reasonable period, except where we must retain certain records to
          meet legal, tax, or security obligations.
        </p>
      </LegalSection>

      <LegalSection title="Your choices and rights">
        <p>
          You can access and update most of your information from within the app, and
          you can request a copy or deletion of your data by contacting us. Depending
          on where you live, you may have additional rights over your personal
          information; we honor those rights as required by applicable law.
        </p>
      </LegalSection>

      <LegalSection title="Children">
        <p>
          {SITE_NAME} is a business tool and is not directed to children under 16. We
          do not knowingly collect personal information from children.
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
      </LegalSection>
    </ArticleShell>
  );
}
