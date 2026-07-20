-- P3b-4 (Lock Clause 5, INACTIVE adapter): the sealed six-state terminal decision the run WOULD receive,
-- recorded beside each shadow assessment. NULL on pre-P3b-4 rows; the column carries the TerminalDecision
-- enum name. Shadow never mutates a run's terminal (Lock Clause 1) — this is parity evidence for P2b.
ALTER TABLE completion_assessment ADD COLUMN IF NOT EXISTS would_be_terminal_decision TEXT NULL;
