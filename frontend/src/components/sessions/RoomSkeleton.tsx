/**
 * A Room-shaped loading placeholder — the header bar + a few turn-header rows shimmering in the real room's shape —
 * so opening a run eases into the thread instead of flashing a bare "Loading…" and then popping the whole room in.
 * Matches the `.room-room` / `.room-head` / `.room-turn` structure so the skeleton and the resolved room align.
 */
export function RoomSkeleton() {
  return (
    <div className="room-skel" role="status" aria-busy="true" aria-label="Loading the session…">
      <div className="room-skel-head">
        <div className="room-skel-line room-skel-crumbs" />
        <div className="room-skel-line room-skel-title" />
        <div className="room-skel-line room-skel-sub" />
      </div>
      <div className="room-skel-thread">
        {[0, 1, 2].map((i) => (
          <div className="room-skel-turn" key={i} style={{ animationDelay: `${i * 80}ms` }}>
            <div className="room-skel-turn-head">
              <div className="room-skel-av" />
              <div className="room-skel-line" style={{ width: 92 }} />
              <div className="room-skel-pill" />
              <div className="room-skel-line room-skel-meta" />
            </div>
            {i === 0 && <div className="room-skel-card" />}
          </div>
        ))}
      </div>
    </div>
  );
}
