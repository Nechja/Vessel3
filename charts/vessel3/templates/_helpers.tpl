{{- define "vessel3.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "vessel3.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "vessel3.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "vessel3.labels" -}}
helm.sh/chart: {{ include "vessel3.chart" . }}
{{ include "vessel3.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "vessel3.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vessel3.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "vessel3.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "vessel3.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{- define "vessel3.authSecretName" -}}
{{- if .Values.auth.secretName -}}
{{- .Values.auth.secretName -}}
{{- else if or .Values.auth.accessKey .Values.auth.secretKey -}}
{{- if not (and .Values.auth.accessKey .Values.auth.secretKey) -}}
{{- fail "auth.accessKey and auth.secretKey must be set together" -}}
{{- end -}}
{{- printf "%s-admin-creds" (include "vessel3.fullname" .) -}}
{{- end -}}
{{- end }}

{{- define "vessel3.image" -}}
{{- $tag := .Values.image.tag | default (printf "%s%s" .Chart.AppVersion (ternary "-ui" "" .Values.image.ui)) -}}
{{- printf "%s:%s" .Values.image.repository $tag -}}
{{- end }}
