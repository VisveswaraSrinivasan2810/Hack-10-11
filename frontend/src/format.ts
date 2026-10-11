const dt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit', hour12: true })
export const fmtDate = (iso: string) => dt.format(new Date(iso))
export const ago = (iso: string) => {
  const h = Math.max(1, Math.round((Date.now() - new Date(iso).getTime()) / 36e5))
  return h < 24 ? `${h} h` : `${Math.round(h / 24)} d`
}
