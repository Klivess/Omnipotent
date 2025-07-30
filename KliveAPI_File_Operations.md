# KliveAPI File Upload and Download Documentation

This document describes the file upload and download functionality added to KliveAPI.

## Overview

KliveAPI now supports file upload and download operations through two new endpoints:
- `POST /files/upload` - Upload one or more files
- `GET /files/download` - Download a previously uploaded file

## Authentication

Both endpoints require authentication with **Associate** level permissions or higher. Include your authorization token in the `Authorization` header.

## File Upload

### Endpoint
```
POST /files/upload
```

### Content-Type
The request must use `multipart/form-data` content type.

### Request Format
Upload files using multipart form data. You can upload multiple files in a single request.

### Example using curl
```bash
curl -X POST http://localhost:5000/files/upload \
  -H "Authorization: your-auth-token" \
  -F "file=@/path/to/your/file.txt" \
  -F "file=@/path/to/another/file.pdf"
```

### Example using JavaScript/HTML
```javascript
const formData = new FormData();
formData.append('file', fileInput.files[0]);

fetch('http://localhost:5000/files/upload', {
    method: 'POST',
    headers: {
        'Authorization': 'your-auth-token'
    },
    body: formData
})
.then(response => response.json())
.then(data => console.log(data));
```

### Security Restrictions

#### Allowed File Types
- `.txt` - Plain text files
- `.pdf` - PDF documents
- `.png`, `.jpg`, `.jpeg`, `.gif` - Image files
- `.doc`, `.docx` - Microsoft Word documents
- `.zip` - ZIP archives

#### File Size Limit
- Maximum file size: **10 MB** per file

### Response Format
```json
{
    "success": true,
    "uploadedFiles": [
        {
            "originalName": "document.pdf",
            "savedName": "2024-01-15_14-30-25_document.pdf",
            "size": 1048576,
            "contentType": "application/pdf",
            "uploadTime": "2024-01-15T14:30:25.123Z"
        }
    ],
    "message": "Successfully uploaded 1 file(s)"
}
```

### Error Response
```json
{
    "success": false,
    "error": "Upload failed"
}
```

## File Download

### Endpoint
```
GET /files/download
```

### Parameters
- `filename` (required) - Name of the file to download (use the `savedName` from upload response)
- `raw` (optional) - Set to `true` for raw binary download, omit for JSON response

### Example Requests

#### JSON Response (Base64 encoded)
```bash
curl "http://localhost:5000/files/download?filename=2024-01-15_14-30-25_document.pdf" \
  -H "Authorization: your-auth-token"
```

#### Raw Binary Download
```bash
curl "http://localhost:5000/files/download?filename=2024-01-15_14-30-25_document.pdf&raw=true" \
  -H "Authorization: your-auth-token" \
  -o downloaded_file.pdf
```

### Response Format

#### JSON Response
```json
{
    "success": true,
    "fileName": "2024-01-15_14-30-25_document.pdf",
    "contentType": "application/pdf",
    "size": 1048576,
    "content": "JVBERi0xLjQKJcOkw7zDssO..." // Base64 encoded file content
}
```

#### Raw Response
Returns the file content directly with appropriate headers:
- `Content-Type`: Determined by file extension
- `Content-Disposition`: `attachment; filename="filename.ext"`

### Error Responses

#### File Not Found
```json
{
    "success": false,
    "error": "File not found"
}
```
HTTP Status: `404 Not Found`

#### Missing Filename
```json
{
    "success": false,
    "error": "Missing filename parameter"
}
```
HTTP Status: `400 Bad Request`

## File Storage

Files are stored in the `SavedData/KliveAPI/UploadedFiles/` directory relative to the application's base directory. Uploaded files are automatically renamed with timestamps to prevent conflicts and ensure uniqueness.

## Security Features

1. **Authentication Required**: Both endpoints require Associate level permissions
2. **File Type Validation**: Only specific file extensions are allowed
3. **File Size Limits**: 10MB maximum per file
4. **Path Sanitization**: Prevents directory traversal attacks
5. **Unique Naming**: Timestamped filenames prevent conflicts and overwriting

## Testing

Two test utilities are provided:

### Python Test Script
A Python script (`test_file_api.py`) that tests both upload and download functionality:
```bash
python3 test_file_api.py
```

### HTML Test Page
An HTML page (`test_file_upload.html`) for browser-based testing with drag-and-drop file upload interface. Open the file in a web browser and ensure the KliveAPI server is running.

## Common Issues

1. **CORS Errors**: If testing from a web browser, ensure CORS headers are properly configured
2. **Authentication Errors**: Verify your authorization token has Associate level permissions or higher
3. **File Type Rejected**: Check that your file extension is in the allowed list
4. **File Too Large**: Ensure files are under 10MB

## Error Codes

- `400 Bad Request` - Invalid request format, missing parameters, or unsupported file type
- `401 Unauthorized` - Authentication required or insufficient permissions
- `404 Not Found` - File not found for download
- `500 Internal Server Error` - Server error during file processing