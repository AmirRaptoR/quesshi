/** The few line icons the admin and play screens use, stroked in the colour of their button. */
export type IconName = "trash" | "eye" | "eye-off" | "close" | "gear" | "edit" | "chevron-back";

const PATHS: Record<IconName, preact.JSX.Element> = {
  trash: (
    <>
      <path d="M4 6.5h16" />
      <path d="M9.5 6.5V4.8a1.3 1.3 0 0 1 1.3-1.3h2.4a1.3 1.3 0 0 1 1.3 1.3v1.7" />
      <path d="M18.2 6.5 17.4 19a1.6 1.6 0 0 1-1.6 1.5H8.2A1.6 1.6 0 0 1 6.6 19L5.8 6.5" />
      <path d="M10.3 10.5v6M13.7 10.5v6" />
    </>
  ),
  eye: (
    <>
      <path d="M2.5 12S6.2 5.5 12 5.5 21.5 12 21.5 12 17.8 18.5 12 18.5 2.5 12 2.5 12Z" />
      <circle cx="12" cy="12" r="2.9" />
    </>
  ),
  "eye-off": (
    <>
      <path d="M3 3l18 18" />
      <path d="M10.1 10.1a2.9 2.9 0 0 0 3.9 3.9" />
      <path d="M9.6 5.8A8.7 8.7 0 0 1 12 5.5c5.8 0 9.5 6.5 9.5 6.5a17.6 17.6 0 0 1-3.2 4" />
      <path d="M6.3 6.9A17.2 17.2 0 0 0 2.5 12S6.2 18.5 12 18.5a9 9 0 0 0 3.4-.65" />
    </>
  ),
  close: <path d="M6 6l12 12M18 6 6 18" />,
  gear: (
    <>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
    </>
  ),
  edit: (
    <>
      <path d="M4 20h4l11-11a2.8 2.8 0 0 0-4-4L4 16v4Z" />
      <path d="M13.5 6.5l4 4" />
    </>
  ),
  // Points to the start of reading order; app.css mirrors it for right-to-left.
  "chevron-back": <path d="M15 6l-6 6 6 6" />,
};

export function Icon({ name, size = "1.05rem" }: { name: IconName; size?: string }) {
  return (
    <svg class="icon" viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" stroke-width="1.7"
      stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">
      {PATHS[name]}
    </svg>
  );
}
