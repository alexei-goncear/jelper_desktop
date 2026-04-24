import argparse
import base64
import json
import mimetypes
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

API_URL = "https://api.openai.com/v1/responses"
TOKEN_ENV_VAR = "OPENAI_API_KEY"
DEFAULT_RESPONSE_MODEL = "gpt-5.4"
DEFAULT_IMAGE_MODEL = "gpt-image-1"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Conversational image edit wrapper around the OpenAI Responses API")
    parser.add_argument("--image", required=True, help="Path to the source image")
    parser.add_argument("--output", required=True, help="Where to store the resulting image")
    parser.add_argument("--prompt", required=True, help="Prompt applied to the input image")
    parser.add_argument("--model", default=DEFAULT_IMAGE_MODEL, help="Image tool model (default: %(default)s)")
    parser.add_argument("--response-model", default=DEFAULT_RESPONSE_MODEL, help="Main responses model (default: %(default)s)")
    return parser.parse_args()


def ensure_api_key() -> str:
    token = os.environ.get(TOKEN_ENV_VAR)
    if not token:
        raise RuntimeError(f"Environment variable {TOKEN_ENV_VAR} is not set.")
    return token


def resolve_image_source(image_arg: str) -> Path:
    path = Path(image_arg).expanduser().resolve()
    if not path.exists():
        raise FileNotFoundError(f"Source image not found: {path}")
    return path


def detect_output_format(output_path: Path) -> str:
    extension = output_path.suffix.lower()
    if extension == ".png":
        return "png"
    if extension == ".webp":
        return "webp"
    if extension in {".jpg", ".jpeg"}:
        return "jpeg"
    raise RuntimeError("Output path must end with .png, .webp, .jpg, or .jpeg.")


def to_data_url(image_path: Path) -> str:
    mime_type = mimetypes.guess_type(image_path.name)[0] or "application/octet-stream"
    encoded = base64.b64encode(image_path.read_bytes()).decode("ascii")
    return f"data:{mime_type};base64,{encoded}"


def build_instruction(user_prompt: str) -> str:
    return (
        "Edit the provided image. Preserve all content, composition, perspective, "
        "lighting, colors, texture, and untouched regions unless the request explicitly "
        "requires a change. Prefer a minimal, local edit rather than regenerating the whole image. "
        f"User request: {user_prompt.strip()}"
    )


def build_request_payload(image_data_url: str, prompt: str, response_model: str, image_model: str, output_format: str) -> dict:
    return {
        "model": response_model,
        "input": [
            {
                "role": "user",
                "content": [
                    {
                        "type": "input_text",
                        "text": build_instruction(prompt),
                    },
                    {
                        "type": "input_image",
                        "image_url": image_data_url,
                        "detail": "high",
                    },
                ],
            }
        ],
        "tools": [
            {
                "type": "image_generation",
                "model": image_model,
                "action": "edit",
                "quality": "high",
                "output_format": output_format,
                "input_fidelity": "high",
            }
        ],
        "tool_choice": {"type": "image_generation"},
    }


def save_bytes(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as fp:
        fp.write(data)


def read_response_body(error: urllib.error.HTTPError) -> str:
    try:
        body = error.read().decode("utf-8", errors="replace")
    except Exception:
        body = str(error)
    return body


def parse_error_message(body: str) -> str:
    try:
        payload = json.loads(body)
    except json.JSONDecodeError:
        return body

    error = payload.get("error")
    if isinstance(error, dict):
        message = error.get("message")
        if isinstance(message, str) and message.strip():
            return message

    if isinstance(error, str) and error.strip():
        return error

    return body


def find_image_generation_call(payload: dict) -> dict:
    output = payload.get("output")
    if not isinstance(output, list):
        raise RuntimeError("Responses API did not return an output array.")

    for item in output:
        if isinstance(item, dict) and item.get("type") == "image_generation_call":
            return item

    raise RuntimeError("Responses API did not return an image_generation_call result.")


def main() -> int:
    args = parse_args()
    api_key = ensure_api_key()
    image_path = resolve_image_source(args.image)
    output_path = Path(args.output).expanduser().resolve()
    output_format = detect_output_format(output_path)
    image_data_url = to_data_url(image_path)

    payload = build_request_payload(
        image_data_url=image_data_url,
        prompt=args.prompt,
        response_model=args.response_model,
        image_model=args.model,
        output_format=output_format,
    )
    body = json.dumps(payload).encode("utf-8")

    request = urllib.request.Request(
        API_URL,
        data=body,
        method="POST",
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
    )

    try:
        with urllib.request.urlopen(request) as response:
            response_payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = read_response_body(error)
        raise RuntimeError(f"OpenAI API returned {error.code}: {parse_error_message(body)}") from error

    image_call = find_image_generation_call(response_payload)
    image_base64 = image_call.get("result")
    if not isinstance(image_base64, str) or not image_base64:
        raise RuntimeError("Responses API did not return image data.")

    image_bytes = base64.b64decode(image_base64)
    save_bytes(output_path, image_bytes)

    revised_prompt = image_call.get("revised_prompt")
    usage = response_payload.get("usage")
    usage_suffix = ""
    if isinstance(usage, dict):
        total_tokens = usage.get("total_tokens")
        if isinstance(total_tokens, int):
            usage_suffix = f", total_tokens={total_tokens}"

    message = f"response_model={args.response_model}, image_model={args.model}{usage_suffix}"
    if isinstance(revised_prompt, str) and revised_prompt.strip():
        message += f", revised_prompt={revised_prompt}"

    result = {
        "status": "ok",
        "output_path": str(output_path),
        "message": message,
    }

    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        error = {
            "status": "error",
            "error": str(exc),
        }
        print(json.dumps(error, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(1)
