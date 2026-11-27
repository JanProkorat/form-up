import { useEffect, useRef, useState, type ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowUp, Image as ImageIcon, Loader2, Lock, Paperclip } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import ImagePreviewChip from '@/components/inbox/ImagePreviewChip';
import { useRequestChatImageUploadUrl } from '@/hooks/useInboxQueries';
import { showApiError, showError } from '@/lib/api-errors';

/** Chat image allowlist — mirrors the server's `ChatImagePolicy.AllowedContentTypes` (jpeg/png/webp
 * only, narrower than the shared avatar/food-image allowlist which also admits heic/heif). This is
 * a client-side pre-check only; the server re-sniffs the bytes and never trusts this. */
const ALLOWED_IMAGE_CONTENT_TYPES = ['image/jpeg', 'image/png', 'image/webp'];
const FILE_PICKER_ACCEPT = ALLOWED_IMAGE_CONTENT_TYPES.join(',');
/** Mirrors the server's `ImageUploadService.MaxImageSizeBytes` (5 MiB). */
const MAX_IMAGE_SIZE_BYTES = 5 * 1024 * 1024;

export interface ComposerSendPayload {
  text: string;
  imageUploadId?: string;
  imageWidth?: number;
  imageHeight?: number;
  clientMessageId?: string;
}

interface PendingImage {
  file: File;
  previewUrl: string;
  width: number;
  height: number;
}

interface Props {
  conversationId: string;
  /** Returns the send mutation's own promise so the composer can defer clearing
   * the draft (text + pending image) until the send actually resolves. */
  onSend: (payload: ComposerSendPayload) => Promise<void>;
  isSending: boolean;
  onTyping?: () => void;
  /** True when the server would refuse a send in this thread; the composer is replaced by a notice. */
  isLocked?: boolean;
}

/** Reads a File's natural pixel dimensions via a throwaway <img> — a layout hint only, never trusted for security. */
function readImageDimensions(previewUrl: string): Promise<{ width: number; height: number }> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
    img.onerror = () => reject(new Error('Failed to read image dimensions'));
    img.src = previewUrl;
  });
}

/**
 * Message composer, pinned to the bottom of the thread pane. Enter sends;
 * Shift+Enter inserts a newline. The paperclip (file attachments) stays
 * disabled for v1; the image button (#1096) stages a jpeg/png/webp file,
 * shows a preview chip, and on send requests a pre-signed upload URL, PUTs
 * the bytes directly to blob storage, then submits the message with the
 * resulting `imageUploadId`.
 */
export default function Composer({ conversationId, onSend, isSending, onTyping, isLocked = false }: Props) {
  const { t } = useTranslation();
  const [value, setValue] = useState('');
  const [pendingImage, setPendingImage] = useState<PendingImage | null>(null);
  const [imageError, setImageError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  // The id of the last attempted send. Reused only while the payload (trimmed text + picked file) is
  // unchanged, so an edited retry is never swallowed by the server's replay.
  const lastAttemptRef = useRef<{ text: string; file: File | null; id: string } | null>(null);
  const requestUploadUrlMutation = useRequestChatImageUploadUrl(conversationId);
  const isUploading = requestUploadUrlMutation.isPending;
  const isBusy = isSending || isUploading;

  // Revoke the staged image's object URL whenever it's replaced or the composer unmounts —
  // otherwise every picked/removed image leaks its blob for the life of the tab.
  useEffect(() => {
    return () => {
      if (pendingImage) {
        URL.revokeObjectURL(pendingImage.previewUrl);
      }
    };
  }, [pendingImage]);

  function removePendingImage() {
    if (pendingImage) {
      URL.revokeObjectURL(pendingImage.previewUrl);
    }
    setPendingImage(null);
    setImageError(null);
  }

  async function handleFileSelected(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    // Reset the input so re-selecting the same file after removing it fires onChange again.
    event.target.value = '';
    if (!file) {
      return;
    }

    if (pendingImage) {
      URL.revokeObjectURL(pendingImage.previewUrl);
      setPendingImage(null);
    }

    if (!ALLOWED_IMAGE_CONTENT_TYPES.includes(file.type)) {
      setImageError(t('apiErrors.INVALID_IMAGE_CONTENT_TYPE'));
      showError('apiErrors.INVALID_IMAGE_CONTENT_TYPE');
      return;
    }
    if (file.size > MAX_IMAGE_SIZE_BYTES) {
      setImageError(t('apiErrors.IMAGE_TOO_LARGE'));
      showError('apiErrors.IMAGE_TOO_LARGE');
      return;
    }

    setImageError(null);
    const previewUrl = URL.createObjectURL(file);
    try {
      const { width, height } = await readImageDimensions(previewUrl);
      setPendingImage({ file, previewUrl, width, height });
    } catch {
      URL.revokeObjectURL(previewUrl);
      setImageError(t('apiErrors.INVALID_IMAGE_CONTENT_TYPE'));
      showError('apiErrors.INVALID_IMAGE_CONTENT_TYPE');
    }
  }

  function clientMessageIdFor(text: string, file: File | null): string {
    const last = lastAttemptRef.current;
    if (last && last.text === text && last.file === file) {
      return last.id;
    }
    const id = crypto.randomUUID();
    lastAttemptRef.current = { text, file, id };
    return id;
  }

  async function submit() {
    const trimmed = value.trim();
    if (isBusy || (!trimmed && !pendingImage)) {
      return;
    }

    if (!pendingImage) {
      try {
        await onSend({ text: trimmed, clientMessageId: clientMessageIdFor(trimmed, null) });
        lastAttemptRef.current = null;
        setValue('');
      } catch {
        // The send mutation's own onError already raised a toast; keep the
        // draft text in place so the user can retry without retyping it.
      }
      return;
    }

    let uploadId: string;
    try {
      const uploadUrlResponse = await requestUploadUrlMutation.mutateAsync({
        contentType: pendingImage.file.type,
        sizeBytes: pendingImage.file.size,
      });

      if (!uploadUrlResponse.uploadUrl || !uploadUrlResponse.uploadId) {
        throw new Error('Upload URL response missing uploadUrl/uploadId');
      }
      uploadId = uploadUrlResponse.uploadId;

      // A plain fetch PUT to the pre-signed MinIO URL — never through the axios instance, which
      // would attach the API's Authorization header and Content-Type this presigned URL doesn't expect.
      const putResponse = await fetch(uploadUrlResponse.uploadUrl, {
        method: 'PUT',
        headers: { 'Content-Type': pendingImage.file.type },
        body: pendingImage.file,
      });
      if (!putResponse.ok) {
        throw new Error(`Image upload PUT failed with status ${putResponse.status}`);
      }
    } catch (error) {
      // Upload-phase failure (request-URL mint or the MinIO PUT itself) — the pending
      // image and caption are kept so the user can retry the same attachment.
      showApiError(error, 'inbox.composer.imageUploadError');
      return;
    }

    try {
      await onSend({
        text: trimmed,
        imageUploadId: uploadId,
        imageWidth: pendingImage.width,
        imageHeight: pendingImage.height,
        clientMessageId: clientMessageIdFor(trimmed, pendingImage.file),
      });
      lastAttemptRef.current = null;
      removePendingImage();
      setValue('');
    } catch {
      // The send mutation's own onError already raised a toast. Keep the pending
      // image + caption staged so the user can retry without re-picking the file
      // (a retry uploads it again under a fresh upload id).
    }
  }

  if (isLocked) {
    return (
      <div className="flex flex-col gap-2 border-t border-border bg-card px-5 py-3.5">
        <p
          id="composer-locked-notice"
          data-testid="composer-locked-notice"
          className="flex items-center gap-2 px-1 text-meta text-muted-foreground"
        >
          <Lock className="size-4 shrink-0" aria-hidden="true" />
          {t('inbox.composer.lockedNotice')}
        </p>
        <Textarea
          disabled
          rows={1}
          aria-describedby="composer-locked-notice"
          aria-label={t('inbox.composer.placeholder')}
          placeholder={t('inbox.composer.placeholder')}
          className="min-h-8 resize-none"
        />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2 border-t border-border bg-card px-5 py-3.5">
      {pendingImage && <ImagePreviewChip previewUrl={pendingImage.previewUrl} onRemove={removePendingImage} />}
      {imageError && (
        <p data-testid="composer-image-error" className="px-1 text-caption text-destructive">
          {imageError}
        </p>
      )}
      <div className="flex items-center gap-2.5">
        <div className="flex min-h-11.5 flex-1 items-center gap-1 rounded-full border border-input bg-background px-2 py-1">
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-8.5 rounded-full text-muted-foreground"
            disabled
            title={t('shell.comingSoon')}
            aria-label={t('inbox.composer.attachFileAriaLabel')}
          >
            <Paperclip className="size-4" aria-hidden="true" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-8.5 rounded-full text-ink-2"
            disabled={isBusy}
            aria-label={t('inbox.composer.attachImageAriaLabel')}
            onClick={() => fileInputRef.current?.click()}
          >
            <ImageIcon className="size-4" aria-hidden="true" />
          </Button>
          <input
            ref={fileInputRef}
            type="file"
            accept={FILE_PICKER_ACCEPT}
            className="hidden"
            onChange={(event) => void handleFileSelected(event)}
          />
          <Textarea
            value={value}
            onChange={(event) => {
              setValue(event.target.value);
              onTyping?.();
            }}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void submit();
              }
            }}
            placeholder={t('inbox.composer.placeholder')}
            rows={1}
            className="min-h-8 flex-1 resize-none border-none bg-transparent px-1 py-1 shadow-none focus-visible:ring-0"
          />
        </div>
        <Button
          type="button"
          size="icon"
          className="size-11.5 shrink-0 rounded-full bg-marker text-on-dark hover:bg-marker/90"
          disabled={(!value.trim() && !pendingImage) || isBusy}
          onClick={() => void submit()}
          aria-label={t('inbox.composer.sendAriaLabel')}
        >
          {isBusy ? (
            <Loader2 className="size-4 animate-spin" aria-hidden="true" />
          ) : (
            <ArrowUp className="size-5" aria-hidden="true" />
          )}
        </Button>
      </div>
    </div>
  );
}
