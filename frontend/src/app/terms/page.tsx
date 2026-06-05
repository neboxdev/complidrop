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
  title: "Terms of Service",
  description:
    "The terms that govern your use of CompliDrop — accounts, acceptable use, billing and cancellation, the limits of automatic document reading, and liability.",
  path: "/terms",
});

const EFFECTIVE_DATE = "June 4, 2026";

export default function TermsOfServicePage() {
  return (
    <ArticleShell>
      <JsonLd
        data={breadcrumbLd([
          { name: "Home", path: "/" },
          { name: "Terms of Service", path: "/terms" },
        ])}
      />
      <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "Terms of Service" }]} />

      <ContentH1>Terms of Service</ContentH1>
      <Lead>
        These terms are the agreement between you and {LEGAL_ENTITY} (&ldquo;{SITE_NAME}
        &rdquo;, &ldquo;we&rdquo;, &ldquo;us&rdquo;) for using our service. By creating
        an account or using {SITE_NAME}, you agree to them. Please read them —
        especially the sections on the limits of automatic document reading and on
        liability.
      </Lead>
      <p className="mt-4 text-sm text-muted-foreground">Last updated {EFFECTIVE_DATE}.</p>

      <LegalSection title="The service">
        <p>
          {SITE_NAME} is software that helps you collect, read, and track compliance
          documents — certificates of insurance, licenses, and permits — check them
          against requirements you define, and send reminders before they expire.
        </p>
      </LegalSection>

      <LegalSection title="Your account">
        <p>
          You must provide accurate information, keep your login credentials secure,
          and you are responsible for activity that happens under your account. You
          must be able to form a binding contract and use {SITE_NAME} for a business
          purpose. Tell us promptly if you suspect unauthorized access.
        </p>
      </LegalSection>

      <LegalSection title="Acceptable use">
        <p>You agree not to:</p>
        <ul className="ml-5 list-disc space-y-1.5">
          <li>upload content you don&apos;t have the right to upload, or that is unlawful;</li>
          <li>attempt to access another customer&apos;s data or break the service&apos;s isolation;</li>
          <li>probe, scan, overload, or disrupt the service or its infrastructure;</li>
          <li>reverse engineer or resell the service except as allowed by law.</li>
        </ul>
      </LegalSection>

      <LegalSection title="Your content">
        <p>
          The documents and data you upload are yours. You grant {SITE_NAME} the
          limited permission needed to host, process, and display that content so we
          can provide the service to you (including sending it to the service
          providers listed in our{" "}
          <Link href="/privacy" className="text-primary hover:underline">
            Privacy Policy
          </Link>
          ). We don&apos;t claim ownership of your content.
        </p>
      </LegalSection>

      <LegalSection title="Automatic reading is a head start, not advice">
        <p>
          {SITE_NAME} uses automated tools to read documents and flag whether they
          appear to meet your requirements. This is a convenience to save you time —
          it is <strong className="text-foreground">not</strong> legal, insurance, or
          professional advice, and we do not guarantee that every extracted value or
          compliance result is accurate or complete.
        </p>
        <p>
          You are responsible for reviewing and confirming the results and for your
          own compliance decisions. We also can&apos;t guarantee that reminder emails
          are delivered, so treat reminders as a helpful nudge, not a guaranteed
          notice. Don&apos;t rely on {SITE_NAME} as your only safeguard against a
          missed expiration or an insufficient policy.
        </p>
      </LegalSection>

      <LegalSection title="Plans, billing, and cancellation">
        <p>
          {SITE_NAME} offers a free tier and paid subscription plans. Current prices
          are shown on our{" "}
          <Link href="/#pricing" className="text-primary hover:underline">
            pricing page
          </Link>{" "}
          and at checkout. The free tier never requires a card and is never charged.
          Paid plans renew automatically each billing period (monthly or annual) until
          you cancel, and you authorize us to charge your payment method through our
          payment processor, Stripe.
        </p>
        <p>
          You can cancel anytime from your account settings. Cancellation takes effect
          at the end of your current paid period, and you keep access until then.
          Except where required by law, fees already paid are non-refundable, and we
          don&apos;t provide partial-period refunds. If a price changes, we&apos;ll
          give you notice before it applies to your next renewal, and for annual plans
          we&apos;ll email you a reminder before the term renews.
        </p>
      </LegalSection>

      <LegalSection title="Our intellectual property">
        <p>
          The {SITE_NAME} software, design, and brand are owned by us. These terms
          don&apos;t grant you any rights to them except the right to use the service
          as described.
        </p>
      </LegalSection>

      <LegalSection title="Disclaimers">
        <p>
          The service is provided &quot;as is&quot; and &quot;as available,&quot;
          without warranties of any kind, whether express or implied, including
          merchantability, fitness for a particular purpose, and non-infringement. We
          don&apos;t warrant that the service will be uninterrupted, error-free, or
          that automatic reading will be accurate.
        </p>
      </LegalSection>

      <LegalSection title="Limitation of liability">
        <p>
          To the fullest extent permitted by law, {SITE_NAME} will not be liable for
          any indirect, incidental, special, consequential, or punitive damages, or
          for lost profits, lost data, or compliance failures. Our total liability for
          any claim relating to the service is limited to the greater of the amount
          you paid us for the service in the twelve months before the claim or
          US$100.
        </p>
      </LegalSection>

      <LegalSection title="Termination">
        <p>
          You can stop using {SITE_NAME} at any time. We may suspend or terminate
          access if you breach these terms or use the service in a way that risks harm
          to {SITE_NAME} or others. You can export your data before your account
          closes; after closure we handle your data as described in the Privacy
          Policy.
        </p>
      </LegalSection>

      <LegalSection title="Governing law">
        <p>
          These terms are governed by the laws of the State of Florida, USA, without
          regard to its conflict-of-laws rules. The state and federal courts located in
          Miami-Dade County, Florida will have jurisdiction over disputes, except where
          applicable law gives you the right to bring a claim elsewhere.
        </p>
      </LegalSection>

      <LegalSection title="Changes to these terms">
        <p>
          We may update these terms as the service evolves. When we make material
          changes we&apos;ll update the date above and, where appropriate, notify you.
          Continuing to use {SITE_NAME} after a change means you accept the updated
          terms.
        </p>
      </LegalSection>

      <LegalSection title="Contact us">
        <p>
          Questions about these terms? Email{" "}
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
