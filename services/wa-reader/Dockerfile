FROM node:22-alpine
WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev --no-audit --no-fund
COPY server.js ./
ENV PORT=8090 DATA_DIR=/data
EXPOSE 8090
CMD ["node", "server.js"]
