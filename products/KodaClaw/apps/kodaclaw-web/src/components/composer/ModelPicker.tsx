import { useEffect, useRef, useState } from "react";
import { Check, ChevronDown } from "lucide-react";

export type ModelOption = { id: string; displayName: string };

type ModelPickerProps = {
  modelName: string;
  selectedModelId?: string | null;
  availableModels?: ModelOption[];
  onModelChange?: (modelId: string) => void;
};

export function ModelPicker({
  modelName,
  selectedModelId,
  availableModels,
  onModelChange,
}: ModelPickerProps) {
  const pillWrapRef = useRef<HTMLDivElement>(null);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const canSwitchModel = (availableModels?.length ?? 0) > 1;

  useEffect(() => {
    if (!dropdownOpen) return;
    function handleOutside(e: MouseEvent) {
      if (pillWrapRef.current && !pillWrapRef.current.contains(e.target as Node)) {
        setDropdownOpen(false);
      }
    }
    document.addEventListener("mousedown", handleOutside);
    return () => document.removeEventListener("mousedown", handleOutside);
  }, [dropdownOpen]);

  return (
    <div ref={pillWrapRef} className="composer__model-pill-wrap">
      <button
        type="button"
        className="composer__model-pill"
        title={modelName}
        onClick={() => canSwitchModel && setDropdownOpen((o) => !o)}
        disabled={!canSwitchModel}
      >
        <span className="composer__model-pill-name">{modelName}</span>
        {canSwitchModel && (
          <ChevronDown
            size={10}
            strokeWidth={2.5}
            className={
              dropdownOpen
                ? "composer__model-pill-chevron composer__model-pill-chevron--open"
                : "composer__model-pill-chevron"
            }
          />
        )}
      </button>
      {dropdownOpen && availableModels && (
        <div className="composer__model-dropdown">
          {availableModels.map((m) => (
            <button
              key={m.id}
              type="button"
              className="composer__model-dropdown-item"
              onClick={() => {
                onModelChange?.(m.id);
                setDropdownOpen(false);
              }}
            >
              <span className="composer__model-dropdown-item-name">{m.displayName}</span>
              {m.id === selectedModelId && <Check size={12} strokeWidth={2.5} />}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
