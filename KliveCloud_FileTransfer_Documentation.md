# KliveCloud File Transfer Interface

This document describes the file transfer interface implemented for the KliveCloud service.

## Overview

The KliveCloud service provides a web-based file transfer interface through the KliveAPI system. It allows authenticated users to upload, download, list, and organize files in a secure cloud storage system.

## API Endpoints

All endpoints require authentication with Associate level permissions or higher.

### 1. List All Files
- **Endpoint**: `GET /klivecloud/allfiles`
- **Description**: Returns a list of all uploaded files with their metadata
- **Response**: JSON array of file metadata objects
- **Example Response**:
```json
[
  {
    "fileId": "12345678901234567890",
    "fileName": "document.pdf",
    "filePath": "/path/to/document.pdf",
    "relativePath": "documents",
    "fileSize": 1024,
    "contentType": "application/pdf",
    "uploadedBy": "username",
    "uploaderUserId": "user123",
    "uploadTime": "2024-01-01T10:00:00Z",
    "fileHash": "abc123..."
  }
]
```

### 2. Create Folder
- **Endpoint**: `POST /klivecloud/makefolder`
- **Parameters**: `path` (query parameter) - the folder path to create
- **Description**: Creates a new folder in the file storage
- **Example**: `POST /klivecloud/makefolder?path=documents/projects`

### 3. Upload Files
- **Endpoint**: `POST /klivecloud/uploadfiles`
- **Content-Type**: `application/json`
- **Description**: Uploads a file with Base64 encoding
- **Request Body**:
```json
{
  "fileName": "example.txt",
  "fileContent": "SGVsbG8gV29ybGQ=",
  "path": "documents"
}
```
- **Response**:
```json
{
  "fileId": "12345678901234567890",
  "message": "File uploaded successfully"
}
```

### 4. Download File
- **Endpoint**: `GET /klivecloud/downloadfile`
- **Parameters**: `fileId` (query parameter) - the ID of the file to download
- **Description**: Downloads a file by its ID
- **Example**: `GET /klivecloud/downloadfile?fileId=12345678901234567890`
- **Response**:
```json
{
  "fileName": "example.txt",
  "contentType": "text/plain",
  "fileSize": 11,
  "fileContent": "SGVsbG8gV29ybGQ="
}
```

## Features

### Security
- Path sanitization prevents directory traversal attacks
- File name sanitization removes invalid characters
- Authentication required for all operations
- User tracking for all uploads

### File Management
- Automatic directory creation
- File metadata tracking
- Content type detection
- File hash calculation for integrity
- Unique file ID generation

### Storage Structure
```
SavedData/
└── KliveCloud/
    ├── Files/          # Actual file storage
    │   └── [user-defined paths]
    └── Metadata/       # File metadata (JSON files)
        └── [fileId].json
```

## Implementation Details

### Services Used
- **KliveAPI**: Provides HTTP routing and authentication
- **DataUtil**: Handles file I/O operations
- **OmniLogging**: Provides logging functionality
- **KMProfileManager**: Handles user authentication

### File Metadata
Each uploaded file has associated metadata stored as JSON:
- Unique file ID
- Original filename and path
- File size and content type
- Uploader information
- Upload timestamp
- File hash for integrity verification

### Error Handling
- Invalid parameters return 400 Bad Request
- Missing files return 404 Not Found
- Server errors return 500 Internal Server Error
- All errors are logged for debugging

## Usage Examples

### JavaScript/Browser
```javascript
// Upload a file
const uploadData = {
    fileName: "test.txt",
    fileContent: btoa("Hello World"), // Base64 encode
    path: "documents"
};

fetch("/klivecloud/uploadfiles", {
    method: "POST",
    headers: {
        "Content-Type": "application/json",
        "Authorization": "your-auth-token"
    },
    body: JSON.stringify(uploadData)
});

// List files
fetch("/klivecloud/allfiles", {
    headers: {
        "Authorization": "your-auth-token"
    }
});
```

### cURL
```bash
# Upload a file
curl -X POST "/klivecloud/uploadfiles" \
  -H "Content-Type: application/json" \
  -H "Authorization: your-auth-token" \
  -d '{"fileName":"test.txt","fileContent":"SGVsbG8gV29ybGQ=","path":"documents"}'

# Download a file
curl "/klivecloud/downloadfile?fileId=12345678901234567890" \
  -H "Authorization: your-auth-token"
```