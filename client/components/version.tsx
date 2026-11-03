/**
 * The release this client was built as. The build writes QUESSHI_VERSION into the bundle; the
 * server reports the same one at /api/health.
 */
export function Version({ class: className = "faint" }: { class?: string }) {
  return <span class={className} dir="ltr">Quesshi {process.env.QUESSHI_VERSION ?? "dev"}</span>;
}
