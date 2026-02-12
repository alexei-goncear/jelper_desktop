import replicate

input = {
    "image": "https://replicate.delivery/pbxt/MKdkS3Po0PXytPbTXh4bOlBX1BZRuXH4o34yXVEakeBlpiTW/blonde_mj.png"
}

output = replicate.run(
    "recraft-ai/recraft-crisp-upscale",
    input=input
)

# To access the file URL:
print(output.url)
#=> "https://replicate.delivery/.../output.webp"

# To write the file to disk:
with open("output.webp", "wb") as file:
    file.write(output.read())
#=> output.webp written to disk

