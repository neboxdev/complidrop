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
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { SITE_NAME, SUPPORT_EMAIL } from "@/lib/site";

export const metadata: Metadata = pageMetadata({
  title: "Contact & Support",
  description:
    "Get help with CompliDrop. Email our support team — we usually reply within one business day.",
  path: "/contact",
});

export default function ContactPage() {
  return (
    <ArticleShell>
      <JsonLd
        data={breadcrumbLd([
          { name: "Home", path: "/" },
          { name: "Contact", path: "/contact" },
        ])}
      />
      <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "Contact" }]} />

      <ContentH1>Contact &amp; support</ContentH1>
      <Lead>
        A real person reads every message. Whether you&apos;re evaluating {SITE_NAME},
        stuck on a document, or have a billing question, email us — we usually reply
        within one business day.
      </Lead>

      <div className="mt-8">
        <a
          href={`mailto:${SUPPORT_EMAIL}`}
          className={cn(buttonVariants({ size: "lg" }), "h-12 px-7 text-base")}
        >
          Email {SUPPORT_EMAIL}
        </a>
      </div>

      <LegalSection title="What we can help with">
        <ul className="ml-5 list-disc space-y-1.5">
          <li>Questions about whether {SITE_NAME} fits your business before you sign up.</li>
          <li>Help uploading documents, reading results, or setting your requirements.</li>
          <li>Billing, plan changes, and cancellations.</li>
          <li>Privacy, data-export, or account-deletion requests.</li>
          <li>Anything that looks wrong — bugs, a misread document, or a reminder that didn&apos;t arrive.</li>
        </ul>
      </LegalSection>

      <LegalSection title="Before you write">
        <p>
          A quick answer might already be on our{" "}
          <Link href="/faq" className="text-primary hover:underline">
            FAQ
          </Link>{" "}
          or in the{" "}
          <Link href="/glossary" className="text-primary hover:underline">
            compliance glossary
          </Link>
          . For account-specific questions, mentioning the email on your account helps
          us find you faster.
        </p>
      </LegalSection>
    </ArticleShell>
  );
}
