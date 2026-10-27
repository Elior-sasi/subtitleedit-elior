# Official conda-forge image; packages are verified against conda channel metadata.
# This is a setup probe, not the production alignment worker.
FROM condaforge/miniforge3:25.3.1-0
RUN conda create --yes --name mfa --override-channels --channel conda-forge \
      python=3.11 montreal-forced-aligner=3.4.3 \
    && conda clean --all --yes
COPY probe.py /app/probe.py
ENTRYPOINT ["conda", "run", "--no-capture-output", "--name", "mfa", "python", "/app/probe.py"]

