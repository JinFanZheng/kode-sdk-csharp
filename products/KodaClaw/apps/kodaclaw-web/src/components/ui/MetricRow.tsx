import type { ReactNode } from "react";

interface MetricRowProps {
  label: string;
  children: ReactNode;
}

/** Two-column metric grid item — reusable across desks. */
export function MetricRow({ label, children }: MetricRowProps) {
  return (
    <div className="metric-item">
      <span className="metric-label">{label}</span>
      <span className="metric-value">{children}</span>
    </div>
  );
}
