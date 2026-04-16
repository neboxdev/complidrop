"use client";

import { Card, CardContent } from "@/components/ui/card";

export default function ExportPage() {
  return (
    <div className="max-w-5xl mx-auto px-6 py-8 space-y-4">
      <h1 className="text-2xl font-semibold text-sky-900">Export</h1>
      <Card>
        <CardContent className="p-8 text-sm text-slate-500">
          PDF audit report + CSV export land in a later phase.
        </CardContent>
      </Card>
    </div>
  );
}
