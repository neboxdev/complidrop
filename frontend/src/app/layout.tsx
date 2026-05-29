import type { Metadata, Viewport } from "next";
import { Plus_Jakarta_Sans } from "next/font/google";
import "./globals.css";
import { Providers } from "@/lib/providers";
import { JsonLd } from "@/components/JsonLd";
import { organizationLd, webSiteLd } from "@/lib/structured-data";
import { SITE_DESCRIPTION, SITE_NAME, SITE_URL } from "@/lib/site";
import { BRAND_COLORS } from "@/lib/brand";

const plusJakartaSans = Plus_Jakarta_Sans({
  variable: "--font-sans",
  subsets: ["latin"],
  display: "swap",
});

export const metadata: Metadata = {
  // `metadataBase` makes social-card image + canonical URLs absolute. The
  // canonical origin and brand facts now live in `@/lib/site` so every SEO
  // surface (metadata, sitemap, robots, manifest, JSON-LD) agrees by
  // construction. Override the origin per-env via NEXT_PUBLIC_SITE_URL.
  metadataBase: new URL(SITE_URL),
  // Title template: child pages pass a bare title and get " | CompliDrop"
  // appended; the default is used for any page that sets none.
  title: {
    template: `%s | ${SITE_NAME}`,
    default: "CompliDrop — COI & Compliance Tracking Software for Small Business",
  },
  description: SITE_DESCRIPTION,
  applicationName: SITE_NAME,
  authors: [{ name: SITE_NAME, url: SITE_URL }],
  creator: SITE_NAME,
  publisher: SITE_NAME,
  // Brand-level OG/Twitter defaults. Per-page `pageMetadata()` overrides these
  // with page-specific copy; pages that set none inherit this. The og:image
  // itself comes from the `opengraph-image` file convention (kept out of here
  // so the file keeps priority). Canonical URLs are set PER PAGE, never here —
  // a layout-level canonical would point every route at "/".
  openGraph: {
    type: "website",
    url: SITE_URL,
    siteName: SITE_NAME,
    locale: "en_US",
    title: "CompliDrop — COI & Compliance Tracking Software for Small Business",
    description: SITE_DESCRIPTION,
  },
  twitter: {
    card: "summary_large_image",
    title: "CompliDrop — COI & Compliance Tracking Software for Small Business",
    description: SITE_DESCRIPTION,
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
      "max-image-preview": "large",
      "max-snippet": -1,
      "max-video-preview": -1,
    },
  },
};

export const viewport: Viewport = {
  themeColor: BRAND_COLORS.sky,
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className={`${plusJakartaSans.variable} h-full antialiased`}>
      <body className="min-h-full flex flex-col font-sans">
        {/* Site-wide structured data: the company + site entities that anchor
            brand recognition for search engines and AI assistants. Page-level
            entities (SoftwareApplication, FAQPage, …) are rendered per page. */}
        <JsonLd data={[organizationLd(), webSiteLd()]} />
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
