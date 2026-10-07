(() => {
    const frontendToken = "8a060bc7-e168-4a6c-bdd6-0df4a5822266";
    const form = document.querySelector("#investigation-form");
    const questionInput = document.querySelector("#question");
    const submitButton = document.querySelector("#submit-button");
    const connectionBadge = document.querySelector("#connection-badge");
    const connectionDot = document.querySelector("#connection-dot");
    const connectionLabel = document.querySelector("#connection-label");
    const serviceStatus = document.querySelector("#service-status");
    const statusIcon = document.querySelector("#status-icon");
    const serviceLabel = document.querySelector("#service-label");
    const statusPulse = document.querySelector("#status-pulse");
    const reviewStatus = document.querySelector("#review-status");
    const reviewStatusDot = document.querySelector("#review-status-dot");
    const reviewStatusLabel = document.querySelector("#review-status-label");
    const emptyResult = document.querySelector("#empty-result");
    const assistantMessage = document.querySelector("#assistant-message");
    const verdict = document.querySelector("#verdict");
    const verdictLabel = document.querySelector("#verdict-label");
    const resultCopy = document.querySelector("#result-copy");
    const resultNotes = document.querySelector("#result-notes");
    const reviewQuestion = document.querySelector("#review-question");
    const reviewQuestionValue = document.querySelector("#review-question-value");
    const decision = document.querySelector("#decision");
    const source = document.querySelector("#source");
    const approvalStatus = document.querySelector("#approval-status");
    const approvalIcon = document.querySelector("#approval-icon");
    const approvalMessage = document.querySelector("#approval-message");
    const approvalNote = document.querySelector("#approval-note");
    const reportForm = document.querySelector("#report-form");
    const reportCaption = document.querySelector("#report-caption");
    const reportQuestion = document.querySelector("#report-question");
    const reportVerdict = document.querySelector("#report-verdict");
    const reportAnswer = document.querySelector("#report-answer");
    const sampleLinks = document.querySelectorAll(".sample-link");
    let activeRequestController = null;

    if (!form || !questionInput || !approvalStatus) {
        return;
    }

    function setServiceStatus(available) {
        connectionBadge.classList.toggle("connection-badge-error", !available);
        connectionDot.classList.toggle("connection-dot-error", !available);
        connectionLabel.textContent = available ? "Backend connected" : "Backend unavailable";

        serviceStatus.classList.toggle("service-status-error", !available);
        statusIcon.classList.toggle("status-icon-error", !available);
        statusIcon.textContent = available ? "\u2713" : "\u2717";
        serviceLabel.textContent = available ? "API inference ready" : "API inference unavailable";
        statusPulse.classList.toggle("status-pulse-error", !available);
    }

    async function checkServiceStatus() {
        try {
            const response = await fetch("/health", {
                cache: "no-store",
                headers: { Accept: "application/json" },
            });
            const payload = await response.json();
            setServiceStatus(response.ok && payload.status === "ok");
        } catch {
            setServiceStatus(false);
        }
    }

    function setWaitingStatus() {
        approvalStatus.classList.remove("approval-status-error");
        approvalStatus.setAttribute("aria-busy", "true");
        approvalIcon.textContent = "...";
        approvalMessage.textContent = "Review status: waiting for an investigation.";
        approvalNote.textContent = "Your investigation is being processed.";
        reviewStatus.classList.remove("review-status-error");
        reviewStatusDot.classList.remove("review-status-dot-error");
        reviewStatusLabel.textContent = "Waiting for review";
        reportForm.hidden = true;
        reportCaption.hidden = true;
    }

    function setReviewResult(question, answer, ok) {
        const statusText = ok ? "Latest review" : "Review unavailable";
        const verdictText = ok ? "Investigation complete" : "Review unavailable";

        emptyResult.hidden = true;
        assistantMessage.hidden = false;
        assistantMessage.classList.toggle("assistant-message-error", !ok);
        verdict.classList.toggle("verdict-success", ok);
        verdict.classList.toggle("verdict-error", !ok);
        verdictLabel.textContent = verdictText;
        resultCopy.textContent = answer;
        resultNotes.textContent = ok
            ? "This summary was returned by the investigation API and should be confirmed by a human reviewer."
            : "Check the API and model-service status before trying again.";

        reviewQuestion.hidden = false;
        reviewQuestionValue.textContent = question;
        reviewStatus.classList.toggle("review-status-error", !ok);
        reviewStatusDot.classList.toggle("review-status-dot-error", !ok);
        reviewStatusLabel.textContent = statusText;
        decision.textContent = verdictText;
        source.textContent = "Investigation API";

        approvalStatus.setAttribute("aria-busy", "false");
        approvalStatus.classList.toggle("approval-status-error", !ok);
        approvalIcon.textContent = ok ? "\u2713" : "\u2717";
        approvalMessage.textContent = ok
            ? "Review status: ready for review."
            : "Review status: unavailable.";
        approvalNote.textContent = ok
            ? "The review remains subject to human confirmation."
            : "Resolve the backend issue and try again.";

        if (ok) {
            reportQuestion.value = question;
            reportVerdict.value = verdictText;
            reportAnswer.value = answer;
            reportForm.hidden = false;
            reportCaption.hidden = false;
        } else {
            reportForm.hidden = true;
            reportCaption.hidden = true;
        }
    }

    function getErrorMessage(payload, fallback) {
        if (payload && typeof payload.error === "string" && payload.error.trim()) {
            return payload.error.trim();
        }
        if (payload && typeof payload.title === "string" && payload.title.trim()) {
            return payload.title.trim();
        }
        if (payload && Array.isArray(payload.response) && payload.response[0]) {
            const entry = payload.response[0];
            if (typeof entry.error === "string" && entry.error.trim()) {
                return entry.error.trim();
            }
        }
        return fallback;
    }

    async function readPayload(response) {
        const contentType = response.headers.get("content-type") || "";
        if (contentType.includes("application/json")) {
            return response.json();
        }

        return { error: (await response.text()).trim() };
    }

    async function investigate(question) {
        if (activeRequestController) {
            activeRequestController.abort();
        }

        const requestController = new AbortController();
        activeRequestController = requestController;
        setWaitingStatus();
        submitButton.disabled = true;
        form.setAttribute("aria-busy", "true");

        if (!question) {
            setReviewResult(question, "Provide a question before starting an investigation.", false);
            if (activeRequestController === requestController) {
                activeRequestController = null;
                submitButton.disabled = false;
                form.setAttribute("aria-busy", "false");
            }
            return;
        }

        try {
            const response = await fetch("/api/fraud", {
                method: "POST",
                headers: {
                    Accept: "application/json",
                    "Content-Type": "application/json",
                    token: frontendToken,
                },
                body: JSON.stringify({ question }),
                signal: requestController.signal,
            });
            const payload = await readPayload(response);
            if (!response.ok) {
                throw new Error(getErrorMessage(payload, `Investigation request failed (${response.status}).`));
            }

            const entry = Array.isArray(payload.response) ? payload.response[0] : null;
            const answer = entry && (entry["Phi-3-mini"] || entry.apertus);
            if (entry && entry.error) {
                throw new Error(entry.error);
            }
            if (typeof answer !== "string" || !answer.trim()) {
                throw new Error("The API returned no investigation summary.");
            }

            setServiceStatus(true);
            setReviewResult(question, answer.trim(), true);
        } catch (error) {
            if (error instanceof DOMException && error.name === "AbortError") {
                return;
            }
            const message = error instanceof Error ? error.message : "The investigation could not be completed.";
            setReviewResult(question, message, false);
        } finally {
            if (activeRequestController === requestController) {
                activeRequestController = null;
                submitButton.disabled = false;
                form.setAttribute("aria-busy", "false");
            }
        }
    }

    form.addEventListener("submit", (event) => {
        event.preventDefault();
        void investigate(questionInput.value.trim());
    });

    sampleLinks.forEach((link) => {
        link.addEventListener("click", (event) => {
            event.preventDefault();
            const question = link.dataset.question;
            if (!question) {
                return;
            }

            questionInput.value = question;
            void investigate(question);
        });
    });

    const queryQuestion = new URLSearchParams(window.location.search).get("question");
    if (queryQuestion) {
        questionInput.value = queryQuestion;
        void investigate(queryQuestion.trim());
    } else {
        void checkServiceStatus();
    }
})();
