# subs-il MFA alignment service: official MFA 3.4.3 from conda-forge + official mfa-models,
# every model file pinned by SHA-256. Build context: this "service" directory.
FROM condaforge/miniforge3:25.3.1-0
RUN conda create --yes --name mfa --override-channels --channel conda-forge \
      python=3.11 montreal-forced-aligner=3.4.3 google-cloud-storage=3.9.0 \
    && conda clean --all --yes
ARG REL=https://github.com/MontrealCorpusTools/mfa-models/releases/download
RUN mkdir /models && cd /models \
 && curl -fsSL -o english_mfa_acoustic_3.1.0.zip "$REL/acoustic-english_mfa-v3.1.0/english_mfa.zip" \
 && curl -fsSL -o english_mfa_3.1.0.dict "$REL/dictionary-english_mfa-v3.1.0/english_mfa.dict" \
 && curl -fsSL -o english_us_mfa_g2p_3.0.0.zip "$REL/g2p-english_us_mfa-v3.0.0/english_us_mfa.zip" \
 && printf '%s\n' \
    '2c08bd4f82c3943dd57ac09aeac99dbce17a1e1bfe9fd932c8d95cd13d971068  english_mfa_acoustic_3.1.0.zip' \
    '975bf9c7791535c5aec57995e0bd2b77eb7ca364d1d974c2134e07bb0f16b079  english_mfa_3.1.0.dict' \
    '9923b38d59a8b3e3e322f225c52523c2a6248e5ffc9fd89be151ade2dc97cb02  english_us_mfa_g2p_3.0.0.zip' \
    | sha256sum -c - \
 && chmod -R a+rX,go-w /models
COPY mfa_service /app/mfa_service
COPY deploy/models.json /app/models.json
RUN useradd --create-home --uid 10001 aligner && mkdir -p /work && chown aligner /work && chmod 700 /work
USER aligner
WORKDIR /app
ENV PATH=/opt/conda/envs/mfa/bin:$PATH MFA_MODELS_FILE=/app/models.json MFA_WORK_ROOT=/work PYTHONDONTWRITEBYTECODE=1
EXPOSE 8080
ENTRYPOINT ["/opt/conda/envs/mfa/bin/python", "-E", "-s", "-m", "mfa_service.api"]

