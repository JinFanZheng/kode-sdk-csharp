import React from "react";
import { Button } from "../ui/Button";
import { pushAutomationResultToChannel } from "../../lib/api";
import type { InboxText } from "./inboxTranslations";

type Props = {
  inboxId: string;
  channels: string[];
  onSuccess: () => void;
  text: InboxText;
};

export function PushToChannelButton({ inboxId, channels, onSuccess, text }: Props) {
  const [pushing, setPushing] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const handlePush = async () => {
    setPushing(true);
    setError(null);
    try {
      await pushAutomationResultToChannel(inboxId, channels);
      onSuccess();
    } catch (e) {
      setError(e instanceof Error ? e.message : text.pushFailed);
    } finally {
      setPushing(false);
    }
  };

  return (
    <div className="inbox-push-approval">
      <Button
        variant="primary"
        onClick={() => void handlePush()}
        disabled={pushing}
      >
        {pushing ? text.pushing : text.pushToChannel}
      </Button>
      {error ? <span className="inbox-push-approval__error">{error}</span> : null}
    </div>
  );
}
