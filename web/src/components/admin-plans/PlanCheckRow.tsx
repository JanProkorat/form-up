import { useId } from 'react';
import { Checkbox } from '@/components/ui/checkbox';

interface Props {
  label: string;
  hint?: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** A labelled checkbox with an optional quiet hint underneath. */
export default function PlanCheckRow({ label, hint, checked, onCheckedChange, disabled = false }: Props) {
  const id = useId();
  const hintId = `${id}-hint`;
  return (
    <div className="flex items-start gap-2.5">
      <Checkbox
        id={id}
        checked={checked}
        disabled={disabled}
        aria-describedby={hint ? hintId : undefined}
        onCheckedChange={(next) => onCheckedChange(next === true)}
        className="mt-0.5 size-4.5"
      />
      <label htmlFor={id} className="flex flex-col gap-0.5 text-copy text-ink">
        <span className="font-medium">{label}</span>
        {hint && (
          <span id={hintId} className="text-meta font-normal text-muted-foreground">
            {hint}
          </span>
        )}
      </label>
    </div>
  );
}
