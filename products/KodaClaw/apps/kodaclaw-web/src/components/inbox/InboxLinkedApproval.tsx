import React from "react";
import { Loader2 } from "lucide-react";
import { Button } from "../ui/Button";
import type { Approval } from "../../types/contracts";
import type { InboxText } from "./inboxTranslations";
import {
  formatInboxKind,
  formatApprovalStatus,
} from "./inboxTranslations";

type Props = {
  linkedApproval: Approval;
  text: InboxText;
  approvalNotes: Record<string, string>;
  pendingApprovalIds: Record<string, boolean>;
  inlineError?: string | null;
  onApprovalDecision: (approvalId: string, approve: boolean) => void;
  onApprovalNoteChange: (approvalId: string, value: string) => void;
};

export function InboxLinkedApproval({
  linkedApproval,
  text,
  approvalNotes,
  pendingApprovalIds,
  inlineError,
  onApprovalDecision,
  onApprovalNoteChange,
}: Props) {
  const isPending = pendingApprovalIds[linkedApproval.id] ?? false;

  return (
    <div className="inbox-approval-section">
      <p className="metric-label">{text.linkedApproval}</p>
      <div className="control-plane-stage-hero__header">
        <div>
          <strong>{linkedApproval.title}</strong>
          <p className="control-plane-compact-copy">{linkedApproval.summary}</p>
        </div>
        <div className="control-plane-chip-row">
          <span className="control-plane-chip">{formatInboxKind(linkedApproval.kind, text)}</span>
          <span className="control-plane-chip">
            {formatApprovalStatus(linkedApproval.status, text)}
          </span>
        </div>
      </div>
      {linkedApproval.sessionId ? (
        <div className="metric-item">
          <span className="metric-label">{text.session}</span>
          <span className="metric-value metric-value--path">
            {linkedApproval.sessionId}
          </span>
        </div>
      ) : null}
      {linkedApproval.status === "Pending" ? (
        <>
          <textarea
            className="kc-textarea"
            data-testid={`approval-note-${linkedApproval.id}`}
            placeholder={text.notePlaceholder}
            rows={2}
            value={approvalNotes[linkedApproval.id] ?? ""}
            disabled={isPending}
            onChange={(e) =>
              onApprovalNoteChange(linkedApproval.id, e.target.value)
            }
          />
          <div className="control-plane-inline-actions">
            <Button
              variant="primary"
              data-testid="approval-approve"
              disabled={isPending}
              onClick={() => void onApprovalDecision(linkedApproval.id, true)}
            >
              {isPending ? <Loader2 size={14} className="icon-spinning" /> : null}
              {text.approve}
            </Button>
            <Button
              variant="secondary"
              data-testid="approval-reject"
              disabled={isPending}
              onClick={() => void onApprovalDecision(linkedApproval.id, false)}
            >
              {isPending ? <Loader2 size={14} className="icon-spinning" /> : null}
              {text.reject}
            </Button>
          </div>
        </>
      ) : null}
      {inlineError ? (
        <p className="desk-feedback desk-feedback--error">{inlineError}</p>
      ) : null}
      {linkedApproval.decisionNote ? (
        <div className="metric-item">
          <span className="metric-label">{text.decisionNote}</span>
          <span className="metric-value">{linkedApproval.decisionNote}</span>
        </div>
      ) : null}
    </div>
  );
}
