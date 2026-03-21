import os
import resend

# Configure your Resend API key via the RESEND_API_KEY environment variable.
resend.api_key = os.environ["RESEND_API_KEY"]

# Email parameters — override via environment variables or edit directly.
FROM_ADDRESS = os.environ.get("EMAIL_FROM", "FlowForge <onboarding@resend.dev>")
TO_ADDRESS = os.environ.get("EMAIL_TO", "delivered@resend.dev")
SUBJECT = os.environ.get("EMAIL_SUBJECT", "FlowForge E2E Test")
HTML_BODY = os.environ.get(
    "EMAIL_HTML",
    "<strong>FlowForge is working!</strong><p>This is an end-to-end test email sent via Resend.</p>",
)

params: resend.Emails.SendParams = {
    "from": FROM_ADDRESS,
    "to": [TO_ADDRESS],
    "subject": SUBJECT,
    "html": HTML_BODY,
}

email = resend.Emails.send(params)
print(email)
