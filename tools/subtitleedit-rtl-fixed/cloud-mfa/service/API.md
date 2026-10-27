# subs-il MFA alignment API (v1)

Montreal Forced Aligner 3.4.3 (MIT) aligns an existing script to speech. It does not
transcribe or translate, and the script is never rewritten.

All `/v1` routes need `Authorization: Bearer <token>`. Tokens are issued per user with
`python -m mfa_service.admin USER`; the server stores only SHA-256 hashes. A job is visible
only to the user who created it; ids are 192-bit random (`[A-Za-z0-9_-]{32}`). Other users get 404.

| Method | Path | Purpose |
|---|---|---|
| GET | `/healthz` | No auth. Engine version, available language ids, worker/queue counts. |
| GET | `/v1/languages` | Installed models (`available`, `verified`, `reason` when unavailable), limits, retention. |
| POST | `/v1/jobs` | Create. Body: `client_request_id` (UUID), `language`, `cues` [{`id` int, `text`}], `audio_sha256`, `audio_bytes`. Same request id + same input returns the same job (200); different input → 409. |
| PUT | `/v1/jobs/{id}/audio/chunks/{n}` | Upload chunk n (≤ 8 MiB, header `X-Chunk-SHA256`). Re-sending an identical chunk is accepted. |
| POST | `/v1/jobs/{id}/audio/complete` | Body `{chunk_count}`. Verifies total size + SHA-256 and that audio is WAV PCM 16 kHz mono 16-bit within the duration limit, then queues. |
| GET | `/v1/jobs/{id}` | Status: `awaiting_upload`, `queued`, `running`, `succeeded`, `failed`, `cancelled`; `stage`, `stage_index`/`stage_count`, `uploaded_chunks` (for resume), `error` {code, message}. |
| POST | `/v1/jobs/{id}/cancel` | Cancel; a running engine process group is killed. |
| GET | `/v1/jobs/{id}/result` | Only after `succeeded`. |
| DELETE | `/v1/jobs/{id}` | Delete everything for the job now. |

Stages are the real pipeline step: `preparing`, `aligning_pass1`, `segmenting`, `aligning_pass2`
(audio ≥ 30 s), `finishing`. No percentage is invented. Failure codes include `no_speech`
(MFA VAD found no speech), `no_alignment` (MFA `NoAlignmentsError`), `engine_failed`, `timeout`,
`invalid_engine_timing`, `interrupted` (server restarted), `upload_window_closed`.

## Pipeline

1. Pass 1: `mfa align` of the whole script against the whole file (beam 100 / retry 400,
   `--no_tokenization`, optional G2P for out-of-dictionary words).
2. `mfa segment_vad --no_speechbrain` (MFA energy VAD). No speech → job fails `no_speech`.
3. Audio ≥ 30 s: consecutive cues are grouped into utterances over the VAD segments their pass-1
   times fall in (a segment holding < 0.5 s or < 15 % of a cue is ignored as drift), written as a
   TextGrid, and aligned again (pass 2). Final times always come from MFA.

## Result `subs-il.mfa-result/1`

```json
{
  "schema": "subs-il.mfa-result/1", "job_id": "…", "status": "complete | needs_review | partial",
  "engine": {"name": "Montreal Forced Aligner", "version": "3.4.3", "acoustic_model": "…", "dictionary": "…", "g2p_model": "…"},
  "language": "english_mfa",
  "input": {"audio_sha256": "…", "audio_duration_s": 95.346, "cues_sha256": "…", "cue_count": 5},
  "method": {"passes": 2, "segmentation": "…", "utterances": 5, "vad_segments": [[1.03, 5.95]], "beam": 100, "retry_beam": 400},
  "summary": {"cues_total": 5, "cues_aligned": 5, "cues_partial": 0, "cues_unaligned": 0, "cues_review": 0,
              "words_total": 203, "words_aligned": 203, "words_missing": 0, "words_oov": 0, "words_g2p": 4, "tokens_not_speech": 1},
  "engine_diagnostics": {"alignment_analysis": [{"begin": 0.93, "end": 6.05, "overall_log_likelihood": -50.1,
     "speech_log_likelihood": -51.3, "phone_duration_deviation": 2.21, "max_running_short_interval": 1, "snr": 7.0,
     "intensity_deviation": null}], "note": "…"},
  "cues": [{"id": 1, "status": "aligned | partial | unaligned", "start": 1.25, "end": 5.66, "review_flags": [],
            "words": [{"index": 0, "text": "<i>HE", "char_start": 0, "char_end": 5, "aligned_as": ["he"],
                       "start": 1.25, "end": 1.4, "flags": []}]}]
}
```

- `text` is the exact original token (punctuation, tags, dashes); `char_start/char_end` are
  Python code-point offsets into the cue text. `aligned_as` is what was sent to MFA (bidi marks,
  edge punctuation and `<…>` / `{\…}` markup removed, casefolded, split on hyphens).
- Word `flags`: `not_speech` (nothing to align, e.g. `♪`), `missing`, `partially_missing`,
  `oov_unknown_pronunciation` (MFA aligned the word as `spn`), `g2p_pronunciation`.
- Cue `review_flags` (`source` is always given): `crosses_long_pause` (spans a ≥ 1.5 s VAD pause),
  `long_word_duration` (one word > 2.5 s), `no_speech_detected`. They are prompts to listen, not
  confidence. MFA has no per-word confidence and none is returned.
- `status`: `complete` = every cue aligned, no review flag. `needs_review` = aligned with flags.
  `partial` = some words or cues have no time. A script that differs from the speech can still
  give `complete` with wrong times; real tests showed this. Always review by ear.

## Limits and retention (environment, defaults)

`MFA_MAX_AUDIO_SECONDS` 3600 · chunk 8 MiB · 5000 cues · 1000 chars/cue · 400k chars total ·
`MFA_MAX_ACTIVE_PER_USER` 2 · `MFA_MAX_QUEUED_JOBS` 10 · `MFA_MAX_CONCURRENT_JOBS` 1 ·
`MFA_JOB_TIMEOUT_SECONDS` 3600 · upload window 1 h · `MFA_RETENTION_HOURS` 6.
Audio and script are deleted when a job ends; the result is deleted on DELETE or expiry; the
bucket lifecycle deletes anything older than 1 day as a backstop. Logs contain job ids, sizes,
durations and error codes only - never script text, audio, tokens or engine output.

