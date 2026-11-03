import { version } from "../../package.json";

/** The release this client was built from; the server reports the same one at /api/health. */
export function Version({ class: className = "faint" }: { class?: string }) {
  return <span class={className} dir="ltr">Quesshi {version}</span>;
}
