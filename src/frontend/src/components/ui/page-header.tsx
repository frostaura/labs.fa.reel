import * as React from "react";
import { cn } from "../../lib/utils";

export interface PageHeaderProps {
  title: React.ReactNode;
  /** One short sentence under the title: what this page is for, in the user's terms. */
  description?: React.ReactNode;
  /** A small line above the title, e.g. the section a page belongs to. */
  eyebrow?: React.ReactNode;
  /** Controls that act on the whole page; they wrap under the title on a phone. */
  actions?: React.ReactNode;
  className?: string;
}

/** The top of every page (WS-9 task 8): one `h1` in the page type step, so headings stay consistent. */
export function PageHeader({ title, description, eyebrow, actions, className }: PageHeaderProps) {
  return (
    <header className={cn("mb-6 flex flex-wrap items-end justify-between gap-x-6 gap-y-3", className)}>
      <div className="min-w-0 space-y-1">
        {eyebrow && <p className="text-caption font-medium uppercase tracking-wider text-3">{eyebrow}</p>}
        <h1 className="text-page text-1">{title}</h1>
        {description && <p className="max-w-prose text-body text-2">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}
