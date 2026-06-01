# Stage 1: Build Angular
FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci --silent
COPY . .
RUN npx ng build --configuration production --output-path=/dist

# Stage 2: Serve with nginx
FROM nginx:alpine AS final
COPY --from=build /dist/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
