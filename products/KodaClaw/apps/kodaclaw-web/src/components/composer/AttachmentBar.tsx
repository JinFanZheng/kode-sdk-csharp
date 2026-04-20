import { X } from "lucide-react";

export type AttachedMedia = {
  mediaId: string;
  previewUrl: string;
  contentType: string;
  uploading?: boolean;
};

type AttachmentBarProps = {
  items: AttachedMedia[];
  onRemove?: (mediaId: string) => void;
};

export function AttachmentBar({ items, onRemove }: AttachmentBarProps) {
  if (items.length === 0) return null;

  return (
    <div className="composer__attachment-bar">
      {items.map((m) => (
        <div key={m.mediaId} className="composer__attachment-thumb">
          {m.uploading ? (
            <div className="composer__attachment-spinner" />
          ) : (
            <img src={m.previewUrl} alt="" draggable={false} />
          )}
          {onRemove && !m.uploading && (
            <button
              type="button"
              className="composer__attachment-remove"
              onClick={() => onRemove(m.mediaId)}
              aria-label="Remove"
            >
              <X size={10} strokeWidth={2.5} />
            </button>
          )}
        </div>
      ))}
    </div>
  );
}
