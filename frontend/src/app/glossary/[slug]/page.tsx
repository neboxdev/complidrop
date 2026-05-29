import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { ArticleShell, Breadcrumbs, ContentCta, ContentH1, Lead } from "@/components/marketing/content";
import { JsonLd } from "@/components/JsonLd";
import { breadcrumbLd, definedTermLd } from "@/lib/structured-data";
import { pageMetadata } from "@/lib/seo";
import { GLOSSARY_TERMS, getGlossaryTerm } from "@/lib/glossary";

interface Props {
  params: Promise<{ slug: string }>;
}

// Statically generate one page per known term; reject any other slug at the
// router level so the dynamic segment can't be probed for arbitrary paths.
export const dynamicParams = false;

export function generateStaticParams() {
  return GLOSSARY_TERMS.map((term) => ({ slug: term.slug }));
}

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { slug } = await params;
  const term = getGlossaryTerm(slug);
  if (!term) return {};
  return pageMetadata({
    title: term.title,
    description: term.definition,
    path: `/glossary/${term.slug}`,
  });
}

export default async function GlossaryTermPage({ params }: Props) {
  const { slug } = await params;
  const term = getGlossaryTerm(slug);
  if (!term) notFound();

  const related = term.related
    .map((relatedSlug) => getGlossaryTerm(relatedSlug))
    .filter((t): t is NonNullable<typeof t> => Boolean(t));

  return (
    <ArticleShell>
      <JsonLd
        data={[
          definedTermLd({ name: term.term, description: term.definition, slug: term.slug }),
          breadcrumbLd([
            { name: "Home", path: "/" },
            { name: "Glossary", path: "/glossary" },
            { name: term.term, path: `/glossary/${term.slug}` },
          ]),
        ]}
      />

      <Breadcrumbs
        items={[
          { name: "Home", href: "/" },
          { name: "Glossary", href: "/glossary" },
          { name: term.term },
        ]}
      />

      <ContentH1>{term.title}</ContentH1>
      <Lead>{term.definition}</Lead>

      {term.sections.map((section) => (
        <section key={section.heading}>
          <h2 className="mt-10 text-2xl font-bold tracking-tight text-foreground">
            {section.heading}
          </h2>
          {section.paragraphs.map((paragraph, index) => (
            <p key={index} className="mt-4 text-base leading-relaxed text-muted-foreground">
              {paragraph}
            </p>
          ))}
        </section>
      ))}

      {related.length > 0 ? (
        <section className="mt-12 border-t border-border pt-8">
          <h2 className="text-sm font-bold uppercase tracking-[0.15em] text-muted-foreground">
            Related terms
          </h2>
          <ul className="mt-4 space-y-2">
            {related.map((rel) => (
              <li key={rel.slug}>
                <Link
                  href={`/glossary/${rel.slug}`}
                  className="font-medium text-primary hover:underline"
                >
                  {rel.term}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <ContentCta />
    </ArticleShell>
  );
}
