interface SparklineProps {
  values: number[]
  width?: number
  height?: number
  className?: string
}

/**
 * The shape of a series, nothing more: no axes, no labels. The number next to
 * it says where it is; this says where it has been.
 */
export function Sparkline({ values, width = 64, height = 18, className }: SparklineProps) {
  if (values.length < 2) return null

  const max = Math.max(...values)
  const min = Math.min(...values)
  const span = max - min || 1
  const step = width / (values.length - 1)
  const points = values
    .map(
      (value, index) =>
        `${(index * step).toFixed(1)},${(height - 1 - ((value - min) / span) * (height - 2)).toFixed(1)}`,
    )
    .join(' ')
  const last = values[values.length - 1] ?? 0
  const lastY = height - 1 - ((last - min) / span) * (height - 2)

  return (
    <svg
      className={className ? `spark ${className}` : 'spark'}
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      aria-hidden="true"
    >
      <polyline className="spark__line" points={points} />
      <circle className="spark__tip" cx={width} cy={lastY} r="1.8" />
    </svg>
  )
}
