"use client";

import { Card, CardContent } from "@/components/ui/card";

export default function VendorsPage() {
  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-4">
      <h1 className="text-2xl font-semibold text-sky-900">Vendors</h1>
      <Card>
        <CardContent className="p-8 text-sm text-slate-500">
          Vendor management + no-login upload portals ship in the next phase.
        </CardContent>
      </Card>
    </div>
  );
}
