import type { Metadata } from "next";
import Link from "next/link";
import { ArticleShell, Breadcrumbs, ContentCta, ContentH1, Lead } from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { GLOSSARY_TERMS } from "@/lib/glossary";

export const metadata: Metadata = pageMetadata({
  title: "Compliance & Insurance Glossary",
  description:
    "Plain-English definitions of the certificate-of-insurance and vendor-compliance terms small businesses run into — COI, ACORD 25, additional insured, certificate holder, and more.",
  path: "/glossary",
});

export default function GlossaryIndexPage() {
  return (
    <ArticleShell>
      <JsonLd
        data={breadcrumbLd([
          { name: "Home", path: "/" },
          { name: "Glossary", path: "/glossary" },
        ])}
      />

      <Breadcrumbs items={[{ name: "Home", href: "/" }, { name: "Glossary" }]} />

      <ContentH1>Compliance &amp; insurance glossary</ContentH1>
      <Lead>
        The certificate-of-insurance world is full of jargon that decides whether
        you&rsquo;re actually covered. Here are the terms small businesses hit
        most often, in plain English &mdash; no insurance degree required.
      </Lead>

      <dl className="mt-12 space-y-10">
        {GLOSSARY_TERMS.map((term) => (
          <div key={term.slug} className="border-t border-border pt-8">
            <dt>
              <Link
                href={`/glossary/${term.slug}`}
                className="text-xl font-semibold text-foreground transition-colors hover:text-primary"
              >
                {term.term}
              </Link>
            </dt>
            <dd className="mt-3 text-base leading-relaxed text-muted-foreground">
              {term.definition}{" "}
              <Link
                href={`/glossary/${term.slug}`}
                className="font-medium text-primary hover:underline"
              >
                Read more&nbsp;&rarr;
              </Link>
            </dd>
          </div>
        ))}
      </dl>

      <ContentCta />
    </ArticleShell>
  );
}
