import { memo } from 'react'
import { store, type DeviceStats } from '../engine/store'
import { useStore } from '../engine/useStore'

/**
 * One tile per device behind the proxy: what it sent, how much of it had to
 * be held, and how identifying the worst of it was. Hovering a tile lights
 * up that device's rows in the traffic list.
 */
export function Devices() {
  const devices = useStore((state) => state.devices)
  const hoveredDevice = useStore((state) => state.hoveredDevice)

  if (devices.length === 0) return null

  return (
    <div className="devices" aria-label="Devices behind the proxy">
      {devices.map((device) => (
        <Tile key={device.clientLabel} device={device} hot={hoveredDevice === device.clientLabel} />
      ))}
    </div>
  )
}

const Tile = memo(function Tile({ device, hot }: { device: DeviceStats; hot: boolean }) {
  const [kind = '', owner = ''] = device.clientLabel.split(' · ')
  const hover = () => {
    store.hoverDevice(device.clientLabel)
  }
  const leave = () => {
    store.hoverDevice(null)
  }

  return (
    <div
      className="device"
      data-hot={hot}
      data-risk={device.maxRisk}
      onMouseEnter={hover}
      onMouseLeave={leave}
      title={`${device.clientLabel} · ${device.clientIp}`}
    >
      <span className="device__dot" aria-hidden="true" />
      <span className="device__who">
        <span className="u-label device__kind">{kind}</span>
        <span className="device__owner">{owner || device.clientLabel}</span>
      </span>
      <span className="device__nums">
        <Num value={device.seen} label="seen" />
        <Num value={device.treated} label="held" tone="alert" />
        <Num value={device.identifiers} label="ids" tone="warm" />
      </span>
    </div>
  )
})

function Num({ value, label, tone }: { value: number; label: string; tone?: 'alert' | 'warm' }) {
  return (
    <span className="device__num" data-tone={tone}>
      <b>{value}</b>
      <span className="u-label">{label}</span>
    </span>
  )
}
