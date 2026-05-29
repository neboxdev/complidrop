/**
 * Shared building blocks for the text-heavy marketing content pages (FAQ,
 * glossary, comparisons, use-case pages): a consistent article shell, visible
 * breadcrumbs, prose typography, and a conversion CTA strip.
 *
 * These keep the content pages visually consistent and DRY without pulling in
 * the Tailwind Typography plugin (not a project dependency). All are server
 * components except where they embed the header island.
 */
import Link from "next/link";
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { MarketingHeader } from "@/components/marketing/site-header";
import { MarketingFooter } from "@/components/marketing/site-footer";

/** Article-width shell with the shared header + footer, for prose pages. */
export function ArticleShell({ children }: { children: React.ReactNode }) {
  return (
    <>
      <MarketingHeader />
      <main className="bg-white">
        <article className="mx-auto max-w-3xl px-4 py-14 sm:px-6 sm:py-20">{children}</article>
      </main>
      <MarketingFooter />
    </>
  );
}

export interface Crumb {
  name: string;
  /** Omit on the current (last) page. */
  href?: string;
}

/**
 * Visible breadcrumb trail. Pair with `breadcrumbLd()` structured data on the
 * page so the trail is both human- and machine-readable.
 */
export function Breadcrumbs({ items }: { items: readonly Crumb[] }) {
  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className="flex flex-wrap items-center gap-x-2 gap-y-1 text-muted-foreground">
        {items.map((item, index) => {
          const isLast = index === items.length - 1;
          return (
            <li key={item.name} className="flex items-center gap-2">
              {item.href && !isLast ? (
                <Link href={item.href} className="transition-colors hover:text-foreground">
                  {item.name}
                </Link>
              ) : (
                <span aria-current="page" className="text-foreground">
                  {item.name}
                </span>
              )}
              {!isLast ? <span aria-hidden="true">/</span> : null}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** Page H1 for content pages. */
export function ContentH1({ children }: { children: React.ReactNode }) {
  return (
    <h1 className="mt-5 text-3xl font-extrabold leading-tight tracking-tight text-foreground sm:text-4xl">
      {children}
    </h1>
  );
}

/** The direct-answer lead paragraph — larger, sits right under the H1. Lead with the answer for AI-citation (see [#176]). */
export function Lead({ children }: { children: React.ReactNode }) {
  return <p className="mt-5 text-lg leading-relaxed text-muted-foreground">{children}</p>;
}

const ctaClass = cn(
  buttonVariants({ size: "lg" }),
  "h-12 cursor-pointer rounded-xl bg-accent px-7 text-base font-semibold text-accent-foreground shadow-md transition-all duration-200 hover:bg-accent/90 hover:shadow-lg",
);

/** Conversion strip rendered at the bottom of content pages to route readers into signup. */
export function ContentCta({
  heading = "Stop tracking certificates in a spreadsheet.",
  body = "CompliDrop reads your COIs, checks the coverage, and reminds you before anything expires. Free for your first 5 documents — no credit card.",
}: {
  heading?: string;
  body?: string;
}) {
  return (
    <section className="mt-16 rounded-2xl bg-[#082F49] p-8 text-center sm:p-10">
      <h2 className="text-2xl font-bold tracking-tight text-white">{heading}</h2>
      <p className="mx-auto mt-3 max-w-xl text-sky-200/80">{body}</p>
      <div className="mt-7 flex justify-center">
        <Link href="/register" className={ctaClass}>
          Get started free &rarr;
        </Link>
      </div>
    </section>
  );
}
