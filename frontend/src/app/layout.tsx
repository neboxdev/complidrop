import type { Metadata } from "next";
import { Plus_Jakarta_Sans } from "next/font/google";
import "./globals.css";
import { Providers } from "@/lib/providers";

const plusJakartaSans = Plus_Jakarta_Sans({
  variable: "--font-sans",
  subsets: ["latin"],
  display: "swap",
});

// `metadataBase` makes social-card image URLs absolute. Defaults to the
// public production origin; override per-env via NEXT_PUBLIC_SITE_URL if needed
// (preview deploys, staging, etc.). Empty string is treated as unset — CI/
// preview environments commonly forward env vars without setting a value, and
// `new URL("")` throws.
const SITE_URL =
  process.env.NEXT_PUBLIC_SITE_URL?.trim() || "https://www.complidrop.com";

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  title: "CompliDrop — Stop Chasing Paper. Start Dropping Docs.",
  description:
    "CompliDrop reads your COIs, licenses, and permits in seconds — pulls the dates, checks the coverage, and tells you before anything expires.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className={`${plusJakartaSans.variable} h-full antialiased`}>
      <body className="min-h-full flex flex-col font-sans">
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
