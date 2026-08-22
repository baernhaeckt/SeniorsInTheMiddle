import { COPY } from '../copy'

export function Mark({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 32 32" fill="none" aria-hidden="true">
      <path
        d="M16 2 4 7v9c0 7.2 5 12.4 12 14 7-1.6 12-6.8 12-14V7L16 2Z"
        stroke="var(--warm)"
        strokeWidth="1.4"
      />
      <path d="M16 2v28c7-1.6 12-6.8 12-14V7L16 2Z" fill="var(--cool)" opacity=".14" />
      <path d="M16 2v28" stroke="var(--ink)" strokeWidth="1" opacity=".7" />
      <rect x="11.5" y="14" width="9" height="3.6" rx="1" fill="var(--alert)" />
    </svg>
  )
}

/** The product name with its emphasised word. Caller picks the heading element. */
export function Wordmark() {
  const [before] = COPY.productName.split(COPY.productEmphasis)
  return (
    <>
      {before}
      <em>{COPY.productEmphasis}</em>
    </>
  )
}
