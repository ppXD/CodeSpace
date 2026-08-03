-- A2: the ledger state an assessment was computed FROM, so a re-sweep can tell "nothing new arrived" apart from
-- "nobody ever looked again". Without it the shadow sweep excluded every run that already had an assessment, which
-- made the append-on-change logic beneath it unreachable — a run was assessed once and evidence arriving afterwards
-- (a reconciler settling a manifest, a grade landing late) could never move the record.
-- NULL on rows written before this column existed; those re-assess once and then carry a watermark.
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS ledger_watermark_json TEXT NULL;
